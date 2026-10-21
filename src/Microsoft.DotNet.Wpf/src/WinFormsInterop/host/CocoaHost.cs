// Minimal on-screen host for the WinForms port on macOS: an NSWindow whose content is an
// NSImageView showing the driver's composited window bitmap, with a poll loop that drains
// NSEvents and routes left-clicks back through the driver (hit-test + WM_LBUTTON dispatch).
// Objective-C runtime P/Invoke, same style as the WPF fork's CocoaWindow. Presentation is a
// CoreGraphics bitmap for now; a WebGPU-texture path is the later swap.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class CocoaHost : IWinFormsHost
{
    private readonly Form _form;
    private readonly object _driver;
    private readonly MethodInfo _getBB, _injectClick, _down, _up, _move, _char, _keyDown, _getPresent, _getScene, _getVersion, _rightDown, _rightUp;
    private volatile IntPtr _window, _imageView;
    // A form shown on a UI thread other than the process's main thread -- a splash screen on its own
    // thread, the commonest case. Windows gives every thread its own windows; AppKit aborts the process
    // if a window is touched anywhere but the main thread. Such a host composes on its own thread and
    // hands every AppKit call to the main queue, asynchronously, so it can neither abort the process
    // nor deadlock a main thread that is itself waiting for the form. It takes no input (AppKit
    // delivers it on the main thread, to the main thread's loop), which is what a splash needs.
    private readonly bool _offMain = pthread_main_np() == 0;
    private volatile bool _closed;

    // The WebGPU present path. It has to be the default: the driver records GPU scenes unless
    // WF_GPU_RASTER/WF_WEBGPU switch it off (XplatUIWebGpu.s_gpuRaster), and then its back buffers are
    // empty, so the CoreGraphics path would present a blank window. It was opt-in (WF_WEBGPU=1, which
    // the gallery's run instructions set) and every WinForms window of an app started without it was
    // white.
    private bool UseWebGpu => _gpuRaster && Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";

    // Off the main thread the surface is made on the main thread and the presenter here, on the
    // form's thread, once it exists: Metal presents from any thread, AppKit only builds on the main one.
    private volatile IntPtr _pendingSurface;
    private Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext _pendingContext;
    private double _pendingScale = 1;
    private WgpuPresenter _wgpu;   // when non-null, present through WebGPU instead of CoreGraphics
    // On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
    private readonly bool _gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") != "0"
        && Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";
    // Present-on-change: skip re-rendering when the driver's paint version and caret blink are unchanged.
    private int _lastVer = -1;
    private bool _lastCaretOn, _lastPresentOk;
    private readonly string _tempPng = Path.Combine(Path.GetTempPath(), "wf-host-" + Guid.NewGuid().ToString("N") + ".png");

    internal CocoaHost(Form form)
    {
        _form = form;
        var xplat = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI");
        _driver = xplat.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Type dt = _driver.GetType();
        _getBB = dt.GetMethod("GetWindowBackBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        _injectClick = dt.GetMethod("InjectClick", BindingFlags.NonPublic | BindingFlags.Instance);
        _down = dt.GetMethod("InjectMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
        _up = dt.GetMethod("InjectMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
        _rightDown = dt.GetMethod("InjectRightDown", BindingFlags.NonPublic | BindingFlags.Instance);
        _rightUp = dt.GetMethod("InjectRightUp", BindingFlags.NonPublic | BindingFlags.Instance);
        _move = dt.GetMethod("InjectMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
        _char = dt.GetMethod("InjectChar", BindingFlags.NonPublic | BindingFlags.Instance);
        _keyDown = dt.GetMethod("InjectKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        _getCaret = dt.GetMethod("GetCaret", BindingFlags.NonPublic | BindingFlags.Instance);
        _getPresent = dt.GetMethod("GetPresentWindows", BindingFlags.NonPublic | BindingFlags.Instance);
        _getScene = dt.GetMethod("GetWindowScene", BindingFlags.NonPublic | BindingFlags.Instance);
        _getVersion = dt.GetMethod("GetPaintVersion", BindingFlags.NonPublic | BindingFlags.Instance);
        // Claim the app's on-screen host slot, so the driver's message loop drives THIS window
        // rather than creating a second one of its own.
        PresentationHost.Attach(this, form);
    }

    // ---- composite the WinForms window tree into one bitmap ----------------------

    private Bitmap Composite()
    {
        var bmp = new Bitmap(_form.Width, _form.Height);
        using var g = Graphics.FromImage(bmp);
        // Walk EVERY visible window by the driver's window tree (not just the form's Control tree),
        // so top-level popups (ComboBox/menu dropdowns are separate WS_POPUP windows) are drawn too.
        // Positions are absolute screen coords; the form is the presentation origin.
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        // First triple is the form itself -> use it as the origin.
        int ox = wins.Length >= 3 ? (int)wins[1] : 0;
        int oy = wins.Length >= 3 ? (int)wins[2] : 0;
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            var bb = (Bitmap)_getBB.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (bb != null) g.DrawImageUnscaled(bb, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy);
        }
        return bmp;
    }

    // Build one GPU layer per visible driver window (form + children + WS_POPUP dropdowns), in the
    // driver's paint order, positioned in form space. This is the per-window GPU-compositing input
    // (the GPU draws each control's bitmap as its own quad, instead of a CPU pre-composite).
    private System.Collections.Generic.List<WgpuLayer> GetLayers(out int ox, out int oy)
    {
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var layers = new System.Collections.Generic.List<WgpuLayer>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            var bb = (Bitmap)_getBB.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (bb != null) layers.Add(new WgpuLayer(bb, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy));
        }
        return layers;
    }

    // Build the per-window RECORDED scenes (form + children + popups) in paint order, positioned in
    // form space — the GPU-raster present input (each window's WebGPU scene, composited in one pass).
    private System.Collections.Generic.List<(object, int, int)> GetScenes(out int ox, out int oy)
        => GetScenes(out ox, out oy, null);

    // With popups given, the windows that are not the form's own -- menus, drop-downs, their
    // shadows -- go there instead of into the list (see the popup layer).
    private System.Collections.Generic.List<(object, int, int)> GetScenes(out int ox, out int oy,
        System.Collections.Generic.List<(object, int, int)> popups)
    {
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var list = new System.Collections.Generic.List<(object, int, int)>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (scene == null) continue;
            var item = (scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy);
            if (popups != null && IsPopup((IntPtr)wins[i])) popups.Add(item);
            else list.Add(item);
        }
        // Embedded non-WinForms content LAST, so it draws over the control whose area it occupies.
        var embedded = EmbeddedScenes.Get(ox, oy);
        if (embedded != null) list.AddRange(embedded);
        return list;
    }

    // The blinking text caret as a form-space rect (null when hidden/off-blink); the backing bitmaps
    // don't contain it, so it's a per-frame overlay quad.
    // The driver's blink clock (restarted when the caret is created, moved or shown, as user32's
    // is); the host's own stopwatch only if the driver has none.
    private MethodInfo _caretBlink;
    private long BlinkMs()
    {
        _caretBlink ??= _driver.GetType().GetMethod("GetCaretBlinkElapsed", BindingFlags.NonPublic | BindingFlags.Instance);
        return _caretBlink != null ? (long)_caretBlink.Invoke(_driver, null) : _blink.ElapsedMilliseconds;
    }

    private Rectangle? GetCaretRect(int ox, int oy)
    {
        if (_getCaret == null) return null;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return null;
        if ((BlinkMs() / 530) % 2 != 0) return null;   // ~530ms blink
        return new Rectangle((int)a[0] - ox, (int)a[1] - oy, Math.Max(1, (int)a[2]), (int)a[3]);
    }

    // Caret on/off state only (no position) — the cheap change-check for present-on-change.
    private bool CaretOn()
    {
        if (_getCaret == null) return false;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return false;
        return (BlinkMs() / 530) % 2 == 0;
    }

    // ---- the form shown and hidden --------------------------------------------------------
    //
    // The window follows the form off screen and back, as the Windows host's does. A dialog that is
    // hidden rather than disposed -- ShowDialog hides it when OK is pressed, and the caller may keep
    // it -- stayed on screen: Workbench's About box could not be dismissed.
    private void OnFormVisibleChanged(object sender, EventArgs e)
    {
        if (_form.IsDisposed) return;
        bool visible = _form.Visible;
        OnMain(() =>
        {
            IntPtr window = _window;
            if (window == IntPtr.Zero || _closed) return;
            if (visible)
            {
                SendVoidPtr(window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            }
            else
            {
                HidePopupLayer();
                SendVoidPtr(window, Sel("orderOut:"), IntPtr.Zero);
            }
        });
        if (visible)
        {
            _lastVer = -1;   // it has no picture from while it was away
            Present();
        }
    }

    // ---- the popup layer ----------------------------------------------------------------
    //
    // Menus and drop-downs are windows of the driver, composited into the form's surface. An
    // ElementHost's WPF tree is a window of its own above that surface (a child window), so a
    // drop-down that reached over it was drawn underneath it: app's Controls gallery opened
    // behind the property panes. Win32 puts a popup above every other window. While the form has
    // popups they are presented in a transparent window of their own, ordered above the form's
    // other child windows, and the pointer over the form goes to them through it; with none it is
    // put away.
    private IntPtr _popupWindow, _popupView;
    private WgpuPresenter _popupWgpu;
    private bool _popupShown;
    private NSRect _popupFrame;

    // Inside a popup window (WS_POPUP) -- a menu, a drop-down, a shadow -- and not the form's own.
    // Other windows the form is composited with (a parentless helper control) stay with the form.
    private bool IsPopup(IntPtr handle)
    {
        Hwnd h = Hwnd.ObjectFromHandle(handle);
        while (h?.parent != null) h = h.parent;
        if (h == null || (_form.IsHandleCreated && h.Handle == _form.Handle)) return false;
        return (h.initial_style & WindowStyles.WS_POPUP) != 0 && h.width > 1 && h.height > 1;
    }

    private NSRect ContentScreenRect()
        => SendRectRect(_window, Sel("contentRectForFrameRect:"), SendRectRet(_window, Sel("frame")));

    /// <summary>The popup layer's frame: the display the window is on, so a popup that hangs past the
    /// window's edge (a drop-down at its foot, a menu near its side) is seen, as on Windows.</summary>
    private NSRect PopupLayerFrame()
    {
        IntPtr screen = Send(_window, Sel("screen"));
        return screen != IntPtr.Zero ? SendRectRet(screen, Sel("frame")) : ContentScreenRect();
    }

    /// <summary>A mouse event's point in the window's content (Cocoa: bottom-left origin), also for
    /// one that came through the popup layer, whose frame is the display's.</summary>
    private NSPoint LocationInContent(IntPtr evt, out bool viaLayer)
    {
        NSPoint loc = SendPointRet(evt, Sel("locationInWindow"));
        if (Send(evt, Sel("window")) == IntPtr.Zero)
        {
            // a move AppKit attributed to no window (see HostUnderPointer): its location is the screen's
            NSRect c = ContentScreenRect();
            viaLayer = _popupShown;
            return new NSPoint { x = loc.x - c.x, y = loc.y - c.y };
        }
        viaLayer = _popupWindow != IntPtr.Zero && Send(evt, Sel("window")) == _popupWindow;
        if (!viaLayer) return loc;
        NSRect layer = SendRectRet(_popupWindow, Sel("frame")), content = ContentScreenRect();
        return new NSPoint { x = loc.x + layer.x - content.x, y = loc.y + layer.y - content.y };
    }

    private void PresentPopups(System.Collections.Generic.List<(object, int, int)> popups, int ox, int oy)
    {
        if (popups.Count == 0 || _window == IntPtr.Zero || !UseWebGpu)
        {
            HidePopupLayer();
            return;
        }
        try
        {
            ShowPopupLayer();
            // The popups are placed against the form; the layer is the display, in Win32 coordinates
            // its origin is (layer.x, primary height - top).
            int lx = (int)Math.Round(_popupFrame.x), ly = (int)Math.Round(PrimaryScreenHeight() - _popupFrame.y - _popupFrame.h);
            var placed = new System.Collections.Generic.List<(object, int, int)>(popups.Count);
            foreach (var (scene, x, y) in popups) placed.Add((scene, x + ox - lx, y + oy - ly));
            if (_popupWgpu != null) _popupWgpu.PresentScenes(placed, null, (int)_popupFrame.w, (int)_popupFrame.h);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[CocoaHost] popup layer failed: " + e.Message);
            HidePopupLayer();
        }
    }

    private void ShowPopupLayer()
    {
        NSRect content = PopupLayerFrame();
        if (_popupWindow == IntPtr.Zero)
        {
            _popupWindow = SendInitWindow(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                content, (nuint)0, (nuint)2, false);   // borderless
            SendVoidBool(_popupWindow, Sel("setReleasedWhenClosed:"), false);
            MakeTransparent(_popupWindow);
            _popupView = SendInitRect(Send(Cls("NSView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = content.w, h = content.h });
            SendVoidBool(_popupView, Sel("setWantsLayer:"), true);
            SendVoidPtr(_popupWindow, Sel("setContentView:"), _popupView);
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ThreadGpu.AcquireForHostWindow();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _popupView);
            SendVoidBool(Send(_popupView, Sel("layer")), Sel("setOpaque:"), false);
            double scale = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(_popupView);
            _popupWgpu = new WgpuPresenter(ctx, surface, (int)content.w, (int)content.h, scale, srgb: _gpuRaster, threadDevice: true);
            _popupWgpu.SetTransparent(true);
            _popupWgpu.Tag = " popups";
            _popupFrame = content;
        }
        if (content.x != _popupFrame.x || content.y != _popupFrame.y || content.w != _popupFrame.w || content.h != _popupFrame.h)
        {
            SendVoidRectBool(_popupWindow, Sel("setFrame:display:"), content, false);
            _popupFrame = content;
        }
        if (_popupShown) return;
        if (s_traceInput) Console.WriteLine($"[cocoa input] popup layer 0x{_popupWindow:x} of '{_form.Name}' shown");
        // Added last, so it is ordered above the child windows already there (again each time: an
        // ElementHost's window added since would cover it).
        if (_popupAttached) SendVoidPtr(_window, Sel("removeChildWindow:"), _popupWindow);
        SendPtrNInt(_window, Sel("addChildWindow:ordered:"), _popupWindow, 1);   // NSWindowAbove
        if (!_popupAttached) SendVoidPtr(_popupWindow, Sel("orderFront:"), IntPtr.Zero);
        _popupAttached = true;
        SendVoidBool(_popupWindow, Sel("setIgnoresMouseEvents:"), false);
        SendVoidDouble(_popupWindow, Sel("setAlphaValue:"), 1.0);
        _popupShown = true;
    }

    // Put away by making it inert, not by ordering it out. Ordered out from inside the press that
    // closed the last popup (a click outside a menu), the window server put it back on screen with
    // the picture it last had: app's Controls gallery came back over the click that had just
    // closed it, and went for good only when the app was deactivated. Cleared, see-through and deaf
    // to the pointer, it is nothing wherever it is ordered.
    private bool _popupAttached;

    private void HidePopupLayer()
    {
        if (!_popupShown) return;
        _popupShown = false;
        try { _popupWgpu?.PresentScenes(new System.Collections.Generic.List<(object, int, int)>(), null, (int)_popupFrame.w, (int)_popupFrame.h); }
        catch { }
        SendVoidDouble(_popupWindow, Sel("setAlphaValue:"), 0.0);
        SendVoidBool(_popupWindow, Sel("setIgnoresMouseEvents:"), true);
        if (s_traceInput) Console.WriteLine($"[cocoa input] popup layer 0x{_popupWindow:x} of '{_form.Name}' hidden");
    }


    private void ClosePopupLayer()
    {
        if (_popupWindow == IntPtr.Zero) return;
        HidePopupLayer();
        if (_popupAttached) SendVoidPtr(_window, Sel("removeChildWindow:"), _popupWindow);
        SendVoidPtr(_popupWindow, Sel("orderOut:"), IntPtr.Zero);
        _popupAttached = false;
        Send(_popupWindow, Sel("close"));
        _popupWindow = _popupView = IntPtr.Zero;
        _popupWgpu = null;
    }

    // ---- window + present -------------------------------------------------------

    // The window's frame and its see-through parts follow the form, as Win32 draws them from the
    // window's styles. A borderless form (a splash screen) had a title bar, and a layered one --
    // whose picture carries its own alpha -- was opaque white where the picture is transparent.
    private bool _transparent;

    private nuint StyleMask()
    {
        const nuint Titled = 1, Closable = 2, Miniaturizable = 4, Resizable = 8, FullSizeContentView = 1 << 15;
        nuint buttons = (_form.ControlBox ? Closable : 0) | (_form.ControlBox && _form.MinimizeBox ? Miniaturizable : 0);
        nuint style = _form.FormBorderStyle switch
        {
            FormBorderStyle.None => 0,   // borderless
            FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow => Titled | buttons | Resizable,
            _ => Titled | buttons,
        };
        // A form that draws its own caption keeps a titled window -- one that resizes, takes the
        // keyboard and has rounded corners -- with its content under the title bar, which is hidden
        // (HideTitleBar).
        if (style != 0 && _ownCaption) style |= FullSizeContentView;
        return style;
    }

    // Whether the form draws its own caption: it says the top of its client area is its caption or
    // its sizing border, as a Win32 form does that takes the non-client area over (WM_NCCALCSIZE)
    // and draws a caption there -- DotNetBar's ribbon form, Office-style windows. Windows then shows
    // one caption, the form's; a native title bar above it was a second. Asked with WM_NCHITTEST.
    private bool _ownCaption;

    private bool DrawsOwnCaption()
    {
        if (_form.FormBorderStyle == FormBorderStyle.None || !_form.IsHandleCreated) return false;
        // Its own caption, or its own sizing border inside the client area: both mean the form has
        // taken the frame over.
        int hit = HitTest(_form.Left + _form.Width / 2, _form.Top + 2);
        return hit == HTCAPTION || hit == HTTOP || hit == HTTOPLEFT || hit == HTTOPRIGHT;
    }

    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    private int HitTest(int x, int y)
    {
        try
        {
            return (int)XplatUI.SendMessage(_form.Handle, Msg.WM_NCHITTEST, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        }
        catch
        {
            return 0;
        }
    }

    private void HideTitleBar(IntPtr window)
    {
        if (!_ownCaption) return;
        SendVoidBool(window, Sel("setTitlebarAppearsTransparent:"), true);
        Send(window, Sel("setTitleVisibility:"), (IntPtr)1);   // NSWindowTitleHidden
        for (int button = 0; button <= 2; button++)            // close, miniaturize, zoom: the form has its own
        {
            IntPtr b = Send(window, Sel("standardWindowButton:"), (IntPtr)button);
            if (b != IntPtr.Zero) SendVoidBool(b, Sel("setHidden:"), true);
        }
    }

    private bool IsLayered()
    {
        Hwnd hwnd = _form.IsHandleCreated ? Hwnd.ObjectFromHandle(_form.Handle) : null;
        return hwnd != null && (hwnd.initial_ex_style & WindowExStyles.WS_EX_LAYERED) != 0;
    }

    private static void MakeTransparent(IntPtr window)
    {
        SendVoidBool(window, Sel("setOpaque:"), false);
        SendVoidPtr(window, Sel("setBackgroundColor:"), Send(Cls("NSColor"), Sel("clearColor")));
        SendVoidBool(window, Sel("setHasShadow:"), false);   // a layered window draws its own, if any
    }

    public void Show()
    {
        if (_offMain)
        {
            double fw = _form.Width, fh = _form.Height;
            int fx = _form.Left, fy = _form.Top;
            string title = _form.Text ?? "WinForms";
            _ownCaption = DrawsOwnCaption();
            _form.VisibleChanged += OnFormVisibleChanged;
            nuint style = StyleMask();
            _transparent = IsLayered();
            OnMain(() =>
            {
                if (_closed) return;
                EnsureApp();
                IntPtr nw = SendInitWindow(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                    CocoaRect(fx, fy, fw, fh), style, (nuint)2, false);
                if (_transparent) MakeTransparent(nw);
                HideTitleBar(nw);
                SendVoidPtr(nw, Sel("setTitle:"), NSStr(title));
                IntPtr nv = SendInitRect(Send(Cls("NSView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = fw, h = fh });
                SendVoidBool(nv, Sel("setWantsLayer:"), true);
                SendVoidPtr(nw, Sel("setContentView:"), nv);
                _imageView = nv;
                _window = nw;
                SendVoidPtr(nw, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
                // As the main thread's forms do: a process the user just started takes the foreground
                // with its first window. A splash screen shown from its own thread came up before any
                // main-thread form, behind the app it was launched from, and was never seen.
                Send(App(), Sel("activateIgnoringOtherApps:"), (IntPtr)1);
                if (UseWebGpu)
                {
                    try
                    {
                        // the creating thread's device (ThreadGpu.cs), though the window presents off it
                        var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ThreadGpu.AcquireForHostWindow();
                        IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, nv);
                        _pendingScale = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(nv);
                        if (_transparent) SendVoidBool(Send(nv, Sel("layer")), Sel("setOpaque:"), false);
                        _pendingContext = ctx;
                        _pendingSurface = surface;
                    }
                    catch (Exception e) { Console.Error.WriteLine("[CocoaHost] WebGPU surface for an off-main form failed: " + e.Message); }
                }
                // Commit the window and its layer now. Core Animation commits at the end of a run
                // loop pass, and this may be running inside a busy main thread's call into the
                // driver (DrainMainWork) with no run loop pass to come for a while: the layer was
                // not on screen, and the form's thread waited out a second on every frame for a
                // drawable that could not come.
                Send(Cls("CATransaction"), Sel("flush"));
            });
            Present();
            return;
        }

        EnsureApp();
        double w = _form.Width, h = _form.Height;
        IntPtr win = Send(Cls("NSWindow"), Sel("alloc"));
        var rect = CocoaRect(_form.Left, _form.Top, w, h);
        _transparent = IsLayered();
        _ownCaption = DrawsOwnCaption();
        win = SendInitWindow(win, Sel("initWithContentRect:styleMask:backing:defer:"), rect, StyleMask(), (nuint)2, false);   // backing Buffered(2)
        if (_transparent) MakeTransparent(win);
        HideTitleBar(win);
        SendVoidPtr(win, Sel("setTitle:"), NSStr(_form.Text ?? "WinForms"));

        IntPtr view = Send(Cls("NSView"), Sel("alloc"));
        view = SendInitRect(view, Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = w, h = h });
        SendVoidBool(view, Sel("setWantsLayer:"), true);   // layer-backed: we set layer.contents = CGImage
        SendVoidPtr(win, Sel("setContentView:"), view);

        _window = win; _imageView = view;
        // What the form asked for. AppKit may constrain it (below the menu bar, onto the screen);
        // the first Pump then hands the real frame back to the form.
        _knownBounds = new Rectangle(_form.Left, _form.Top, _form.Width, _form.Height);
        ObserveLiveResize();
        s_live.Add(this);
        _form.VisibleChanged += OnFormVisibleChanged;
        SendVoidPtr(win, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        Send(App(), Sel("activateIgnoringOtherApps:"), (IntPtr)1);

        // WebGPU present path (on unless WF_WEBGPU=0): create a wgpu context, then a CAMetalLayer surface over
        // this NSView (the mac branch of the cross-platform surface abstraction; Windows would use an
        // HWND source, the browser a canvas), then present each composite as a textured quad.
        if (UseWebGpu)
        {
            // the UI thread's device (ThreadGpu.cs), shared with its WPF compositor and Direct3D shim devices
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ThreadGpu.AcquireForHostWindow();
            // Cross-platform surface creation: NativePlatform dispatches to the CAMetalLayer (mac),
            // HWND (Windows), Xlib (Linux) or canvas (browser) source by detected OS. Here we pass the
            // NSView*; a Win32 host passes its HWND. The present path below is identical everywhere.
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _imageView);
            if (surface == IntPtr.Zero) throw new InvalidOperationException("NativePlatform.CreateWindowSurface returned null");
            // Render at the real backing scale (2x on Retina) so text/geometry are crisp rather than a
            // 1x surface upscaled by the display. The CAMetalLayer's contentsScale (set by CreateSurface)
            // and the surface config must both be device-pixel sized; the presenter scales the scene.
            double scale = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(_imageView);
            // Scene path uses an sRGB surface so the renderer's gamma-correct glyph coverage kicks in
            // (crisp, WPF-weight text); the bitmap path stays non-sRGB (pixels are already display-space).
            _wgpu = new WgpuPresenter(ctx, surface, _form.Width, _form.Height, scale, srgb: _gpuRaster, threadDevice: true);
            if (_transparent)
            {
                SendVoidBool(Send(_imageView, Sel("layer")), Sel("setOpaque:"), false);
                _wgpu.SetTransparent(true);
            }
            if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
                Console.Error.WriteLine($"WebGPU present path active (surface 0x{surface:x}, format {_wgpu.Format}, scale {scale})");
            // Let embedded non-WinForms content (an ElementHost's WPF tree) reach the real window and
            // its backing scale now that both exist. Inert when nothing is embedded.
            EmbeddedScenes.PublishHostWindow(_imageView, (float)scale, _form);
        }

        Present();
    }

    public void Present()
    {
        if (_wgpu != null && _gpuRaster)
        {
            // Runtime DPI detection: re-read the window's backing scale each present; if it changed
            // (e.g. the window moved to a Retina/non-Retina display, or the startup value was stale),
            // resync the CAMetalLayer contentsScale + resize the swap chain so text stays crisp
            // (a wrong 1x on a 2x display would upscale to a pixelated result).
            double curScale = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(_imageView);
            if ((float)curScale != _wgpu.Scale)
            {
                Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.SetContentsScale(_imageView, curScale);
                _wgpu.SetScale(curScale);
                _lastVer = -1;   // force a present at the new scale
            }
            // GPU-raster mode: composite each window's RECORDED scene in one pass (no per-control
            // readback / bitmap re-upload). Present ONLY when something changed (driver paint version)
            // or the caret blink toggled — and keep retrying while a present fails (e.g. Occluded until
            // the window is front-most). This makes an idle window cost ~nothing.
            // Fold in the embedded contributors' version too — a hosted WPF tree animates on its own
            // clock and would otherwise never repaint (see EmbeddedScenes).
            int ver = (int)_getVersion.Invoke(_driver, null) + EmbeddedScenes.CurrentVersion();
            bool caretOn = CaretOn();
            string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
            bool wantSave = !string.IsNullOrEmpty(save) && !_savedGpu;
            if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk && !wantSave)
                return;   // nothing changed

            var popups = _offMain ? null : new System.Collections.Generic.List<(object, int, int)>();
            var scenes = GetScenes(out int ox, out int oy, popups);
            Rectangle? caret = GetCaretRect(ox, oy);
            _lastPresentOk = _wgpu.PresentScenes(scenes, caret, _form.Width, _form.Height);
            if (!_lastPresentOk && _offMain) _lastPresentOk = PresentAsLayerContents(scenes, caret);
            else if (_lastPresentOk && _surfaceLayerHidden)
            {
                // a drawable again: the surface shows the frames
                IntPtr view = _imageView;
                OnMain(() => { if (_imageView != IntPtr.Zero && !_closed) { SetSurfaceLayerHidden(Send(view, Sel("layer")), false); Send(Cls("CATransaction"), Sel("flush")); } });
            }
            if (popups != null) PresentPopups(popups, ox, oy);
            _lastVer = ver; _lastCaretOn = caretOn;
            if (Environment.GetEnvironmentVariable("WF_TRACE") != null) Console.Error.WriteLine("[present]");
            if (wantSave)
            {
                _savedGpu = true;
                byte[] rgba = _wgpu.RenderScenesToRgba(scenes, caret);
                SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, save);   // device-pixel sized
                Console.WriteLine($"saved GPU-rendered frame -> {save}");
            }
            return;
        }
        if (_wgpu != null)
        {
            var layers = GetLayers(out int ox, out int oy);
            Rectangle? caret = GetCaretRect(ox, oy);
            _wgpu.PresentLayers(layers, caret, _form.Width, _form.Height);   // one GPU quad per window
            // One-shot: dump the GPU's own output (offscreen readback of the same layered scene).
            string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
            if (!string.IsNullOrEmpty(save) && !_savedGpu)
            {
                _savedGpu = true;
                byte[] rgba = _wgpu.RenderLayersToRgba(layers, caret);
                SaveRgbaPng(rgba, _form.Width, _form.Height, save);
                Console.WriteLine($"saved GPU-rendered frame -> {save}");
            }
            return;
        }
        if (_offMain)
        {
            if (_wgpu == null && _pendingSurface != IntPtr.Zero && !_closed)
            {
                _wgpu = new WgpuPresenter(_pendingContext, _pendingSurface, _form.Width, _form.Height, _pendingScale, srgb: _gpuRaster, threadDevice: true);
                _wgpu.SetTransparent(_transparent);
                _lastVer = -1;
                if (Environment.GetEnvironmentVariable("WF_TRACE_SURFACE") == "1") Console.WriteLine($"[CocoaHost] off-main presenter for '{_form.Text}' {_form.Width}x{_form.Height}");
            }
            if (_wgpu != null && _gpuRaster && !_closed)
            {
                int v = (int)_getVersion.Invoke(_driver, null);
                bool on = CaretOn();
                if (v == _lastVer && on == _lastCaretOn && _lastPresentOk) return;
                var offScenes = GetScenes(out int sx, out int sy);
                Rectangle? offCaret = GetCaretRect(sx, sy);
                _lastPresentOk = _wgpu.PresentScenes(offScenes, offCaret, _form.Width, _form.Height);
                // No drawable. AppKit refreshes a window's occlusion state only when the main thread
                // handles window-server events, and wgpu hands out no drawable to a window it thinks
                // is hidden: a form on its own thread -- a splash screen -- while the main thread is
                // busy loading the app was on screen and stayed blank all its life. Draw the frame
                // offscreen and give it to the layer as its contents, which needs no drawable.
                if (!_lastPresentOk) _lastPresentOk = PresentAsLayerContents(offScenes, offCaret);
                _lastVer = v; _lastCaretOn = on;
                return;
            }
            if (!UseWebGpu) PresentOffMain();
            return;
        }

        // Wrap the composite's raw premultiplied-BGRA bytes in a CGBitmapContext (a direct pixel
        // wrap -- NO ImageIO decode, which SIGBUSes under .NET's signal handler) -> CGImage ->
        // the layer-backed view's contents. A CGImage set as layer.contents displays top-down, and
        // the WinForms bitmap is top-down too, so no flip. CGBitmapContextCreateImage copies the
        // pixels, so the locked buffer only needs to be valid for that call.
        using Bitmap composite = Composite();
        DrawCaret(composite);
        int w = composite.Width, h = composite.Height;
        BitmapData bd = composite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            IntPtr cs = CGColorSpaceCreateDeviceRGB();
            // kCGImageAlphaPremultipliedFirst(2) | kCGBitmapByteOrder32Little(8192) = BGRA.
            IntPtr ctx = CGBitmapContextCreate(bd.Scan0, (nuint)w, (nuint)h, 8, (nuint)bd.Stride, cs, 8194);
            IntPtr cg = CGBitmapContextCreateImage(ctx);
            IntPtr layer = Send(_imageView, Sel("layer"));
            SendVoidPtr(layer, Sel("setContents:"), cg);
            // No standard NSRunLoop is running (we poll events), so implicit CATransactions never
            // commit -- layer.contents changes would batch and the transient pressed frame would be
            // overwritten before display. Force a flush so each frame reaches the window server now.
            Send(Cls("CATransaction"), Sel("flush"));
            CGImageRelease(cg); CGContextRelease(ctx); CGColorSpaceRelease(cs);
        }
        finally { composite.UnlockBits(bd); }
    }

    private byte[] _scratch;
    private bool _savedGpu;
    private int _tracedContents, _savedContents;
    private static readonly bool s_traceSurface = Environment.GetEnvironmentVariable("WF_TRACE_SURFACE") == "1";

    /// <summary>On the main thread: a frame made off it becomes the window's layer contents.</summary>
    private void ApplyLayerContents(IntPtr cg, long queuedAt)
    {
        IntPtr view = _imageView;
        if (view != IntPtr.Zero && !_closed)
        {
            IntPtr layer = Send(view, Sel("layer"));
            SendVoidPtr(layer, Sel("setContents:"), cg);
            // The surface's Metal layer is a sublayer, composited ABOVE these contents, and it keeps
            // showing the last frame it was given: the splash's fade-in drew nothing that could be seen.
            // Out of the way while frames come this way; back for the next drawable (SurfaceLayerHidden).
            SetSurfaceLayerHidden(layer, true);
            Send(Cls("CATransaction"), Sel("flush"));
            if (s_traceSurface && _tracedContents++ < 5)
            {
                IntPtr subs = Send(layer, Sel("sublayers"));
                Console.WriteLine($"surface: layer contents set for '{_form.Text}' after {Environment.TickCount64 - queuedAt} ms on {Marshal.PtrToStringUTF8(object_getClassName(layer))} " +
                                  $"sublayers={(subs == IntPtr.Zero ? 0 : (nint)Send(subs, Sel("count")))} windowVisible={(nint)Send(_window, Sel("isVisible")) != 0} alpha={SendDouble(_window, Sel("alphaValue"))} " +
                                  $"frame={F(SendRectRet(_window, Sel("frame")))} layerFrame={F(SendRectRet(layer, Sel("frame")))} hidden={(nint)Send(layer, Sel("isHidden")) != 0} " +
                                  $"level={(nint)Send(_window, Sel("level"))} occlusion={(nint)Send(_window, Sel("occlusionState"))} opaque={(nint)Send(_window, Sel("isOpaque")) != 0} number={(nint)Send(_window, Sel("windowNumber"))}");
                static string F(NSRect r) => $"{r.x},{r.y} {r.w}x{r.h}";
            }
        }
        CGImageRelease(cg);
    }

    [DllImport(ObjC)] private static extern IntPtr object_getClassName(IntPtr obj);

    private volatile bool _surfaceLayerHidden;

    private void SetSurfaceLayerHidden(IntPtr layer, bool hidden)
    {
        if (layer == IntPtr.Zero || _surfaceLayerHidden == hidden) return;
        IntPtr subs = Send(layer, Sel("sublayers"));
        nint n = subs == IntPtr.Zero ? 0 : (nint)Send(subs, Sel("count"));
        for (nint i = 0; i < n; i++)
        {
            // the view's sublayers are the surface's (wgpu's layer, whatever its class)
            IntPtr sub = Send(subs, Sel("objectAtIndex:"), (IntPtr)i);
            SendVoidBool(sub, Sel("setHidden:"), hidden);
            if (s_traceSurface) Console.WriteLine($"surface: sublayer {Marshal.PtrToStringUTF8(object_getClassName(sub))} hidden={hidden}");
        }
        _surfaceLayerHidden = hidden;
    }

    // The off-main-thread present: composite here (System.Drawing and CoreGraphics bitmaps are thread
    // safe), then set the layer's contents on the main thread. Only when the content changed: the
    // driver's loop calls Present on every iteration, and each one would otherwise be a main-queue hop.
    private bool PresentAsLayerContents(System.Collections.Generic.IReadOnlyList<(object, int, int)> scenes, Rectangle? caret)
    {
        byte[] rgba = _wgpu.RenderScenesToRgba(scenes, caret);
        SetLayerContents(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight);
        if (s_traceSurface)
        {
            int opaque = 0, maxA = 0;
            for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] != 0) opaque++; if (rgba[i] > maxA) maxA = rgba[i]; }
            Console.WriteLine($"surface: no drawable for '{_form.Text}'; presented as layer contents {_wgpu.DeviceWidth}x{_wgpu.DeviceHeight} scenes={scenes.Count} nonTransparent={opaque} maxAlpha={maxA}");
            if (Environment.GetEnvironmentVariable("WF_TRACE_SURFACE_SAVE") is { Length: > 0 } dir && _savedContents++ < 8)
                SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, System.IO.Path.Combine(dir, $"contents-{_form.Handle.ToInt64():x}-{_savedContents}.png"));
        }
        return true;
    }

    /// <summary>Shows <paramref name="rgba"/> (R,G,B,A rows, premultiplied) as the window's layer
    /// contents, from any thread.</summary>
    private unsafe void SetLayerContents(byte[] rgba, int w, int h)
    {
        IntPtr cg;
        fixed (byte* p = rgba)
        {
            IntPtr cs = CGColorSpaceCreateDeviceRGB();
            IntPtr ctx = CGBitmapContextCreate((IntPtr)p, (nuint)w, (nuint)h, 8, (nuint)(w * 4), cs, 1);   // premultiplied RGBA
            cg = CGBitmapContextCreateImage(ctx);    // copies the pixels
            CGContextRelease(ctx); CGColorSpaceRelease(cs);
        }
        long queuedAt = Environment.TickCount64;
        OnMain(() => ApplyLayerContents(cg, queuedAt));
    }

    private void PresentOffMain()
    {
        int ver = (int)_getVersion.Invoke(_driver, null);
        bool caretOn = CaretOn();
        if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk) return;
        _lastVer = ver; _lastCaretOn = caretOn;
        if (_imageView == IntPtr.Zero) { _lastPresentOk = false; return; }   // the window is still being made

        IntPtr cg;
        using (Bitmap composite = Composite())
        {
            DrawCaret(composite);
            int w = composite.Width, h = composite.Height;
            BitmapData bd = composite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                IntPtr cs = CGColorSpaceCreateDeviceRGB();
                IntPtr ctx = CGBitmapContextCreate(bd.Scan0, (nuint)w, (nuint)h, 8, (nuint)bd.Stride, cs, 8194);
                cg = CGBitmapContextCreateImage(ctx);    // copies the pixels
                CGContextRelease(ctx); CGColorSpaceRelease(cs);
            }
            finally { composite.UnlockBits(bd); }
        }
        _lastPresentOk = true;
        long queuedAt = Environment.TickCount64;
        OnMain(() => ApplyLayerContents(cg, queuedAt));
    }

    // ---- the window frame and the form's bounds -----------------------------------------
    //
    // On Windows the form's bounds ARE the window's: the user dragging or resizing it is a WM_MOVE /
    // WM_SIZE, and Form.Location/Size set from code moves the window. Here they are two things kept
    // in step: a change on either side since the last agreement is applied to the other. The user
    // side has to reach the form for anything that follows the window -- layout (a docked property
    // grid at the right edge), Move/Resize handlers, an ElementHost's own window, screen coordinates.

    private Rectangle _knownBounds;
    // A press that started in the form: the drag and release that follow are the form's too, even
    // past its edges (WinForms captures the mouse).
    private bool _formHasMouse;
    private static readonly bool s_traceInput = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";

    // ---- live resize ---------------------------------------------------------------------
    //
    // While the user drags a window edge AppKit runs its own tracking loop and this host's Pump does
    // not run until the mouse is released: the form kept its old layout and an ElementHost's window
    // (a separate NSWindow) stuck out past the shrinking edge, then snapped back. AppKit does post
    // NSWindowDidResizeNotification synchronously from inside that loop, so the frame is carried to
    // the form there -- WinForms lays out synchronously in SetBounds -- the embedded windows follow,
    // and a frame is presented. Events are deliberately not pumped from here: dequeuing inside
    // AppKit's tracking loop would take the mouse-up it is waiting for.

    private IntPtr _resizeBlock, _resizeObserver;
    private GCHandle _self;
    // WF_TRACE_RESIZE=1 (diagnostics): each resize callback; one entered inside another (AppKit's
    // tracking loop re-entered) with the stack that got there
    private static readonly bool s_traceResize = Environment.GetEnvironmentVariable("WF_TRACE_RESIZE") == "1";
    private static int s_inResize;

    private static void TraceResize(string what, bool stack = false)
    {
        if (s_traceResize) Console.Error.WriteLine($"[resize] {what}{(stack ? Environment.NewLine + Environment.StackTrace : "")}");
    }

    private unsafe void ObserveLiveResize()
    {
        if (_window == IntPtr.Zero || _resizeObserver != IntPtr.Zero) return;
        IntPtr center = Send(Cls("NSNotificationCenter"), Sel("defaultCenter"));
        if (center == IntPtr.Zero) return;
        _self = GCHandle.Alloc(this);
        _resizeBlock = MS.Internal.Interop.ObjCBlock.Create(
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnWindowResized, GCHandle.ToIntPtr(_self));
        if (_resizeBlock == IntPtr.Zero) return;
        // A nil queue delivers on the posting thread, synchronously: inside the tracking loop.
        _resizeObserver = SendObserver(center, Sel("addObserverForName:object:queue:usingBlock:"),
                                       NSStr("NSWindowDidResizeNotification"), _window, IntPtr.Zero, _resizeBlock);
    }

    [UnmanagedCallersOnly]
    private static void OnWindowResized(IntPtr block, IntPtr notification)
    {
        // Inside AppKit: nothing may unwind through its frames.
        try
        {
            IntPtr context = MS.Internal.Interop.ObjCBlock.ContextOf(block);
            if (context == IntPtr.Zero || GCHandle.FromIntPtr(context).Target is not CocoaHost host) return;
            if (host._window == IntPtr.Zero || host._form.IsDisposed) return;
            if (s_traceResize) TraceResize($"enter native={host.NativeBounds()} form={host._form.Bounds} depth={s_inResize}", stack: s_inResize == 1);
            s_inResize++;
            try
            {
                host.SyncFrame();
                EmbeddedScenes.Pump();
                host.Present();
            }
            finally { s_inResize--; }
            if (s_traceResize) TraceResize($"leave native={host.NativeBounds()} form={host._form.Bounds}");
        }
        catch (Exception e)
        {
            TraceResize("callback threw: " + e);
        }
    }

    private void StopObservingLiveResize()
    {
        if (_resizeObserver != IntPtr.Zero)
        {
            IntPtr center = Send(Cls("NSNotificationCenter"), Sel("defaultCenter"));
            if (center != IntPtr.Zero) SendVoidPtr(center, Sel("removeObserver:"), _resizeObserver);
            _resizeObserver = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendObserver(IntPtr r, IntPtr s, IntPtr name, IntPtr obj, IntPtr queue, IntPtr block);

    // ---- activation ---------------------------------------------------------------------
    //
    // The form is the active window while its window is AppKit's key window, or while the key window
    // is one of its own (an ElementHost's WPF window, which on Windows is a child window of the form
    // and takes nothing from it). The driver hears of it as Windows tells a form, WM_ACTIVATE, and
    // knows the active form from it. It never heard: Form.ActiveForm was always null, and a dialog
    // shown with ShowDialog() and no owner -- which takes the active window for its owner on
    // Windows -- had none, so one centred on its parent (app's About box) stayed at 0,0.

    private bool _reportedActive;
    private static MethodInfo s_hostActivate;

    private void ReportActivation()
    {
        if (_window == IntPtr.Zero || !_form.IsHandleCreated) return;
        IntPtr key = Send(App(), Sel("keyWindow"));
        bool active = key != IntPtr.Zero && (key == _window || Send(key, Sel("parentWindow")) == _window);
        if (active == _reportedActive) return;
        _reportedActive = active;
        s_hostActivate ??= _driver.GetType().GetMethod("HostActivate", BindingFlags.NonPublic | BindingFlags.Instance);
        s_hostActivate?.Invoke(_driver, new object[] { _form.Handle.ToInt64(), active });
    }

    // ---- the screens ----------------------------------------------------------------------
    //
    // The screens WinForms knows are the Mac's displays, as the Windows host's are the monitors:
    // bounds from each NSScreen's frame, the working area from its visible frame (without the menu
    // bar and the Dock), in Win32's coordinates. They were the main window's area, so that a popup,
    // drawn inside the window, would not be placed past its edge; but a form is a window of its own,
    // and a splash screen or a dialog centred on that "screen" opened off centre -- before the main
    // window existed, on an imaginary 2560x1440 desktop. Popups now have a layer the size of the
    // display (the popup layer), so one past the window's edge is seen, as on Windows.

    private static Screen[] s_screens = Array.Empty<Screen>();
    private static long s_screensAt;

    /// <summary>Called by the driver when it starts: the screens are known before any form places
    /// itself (a splash screen, on a thread of its own, before any window exists).</summary>
    internal static void InstallScreens() => XplatUIWebGpu.ScreensProvider ??= MacScreens;

    private static Screen[] MacScreens()
    {
        if (pthread_main_np() != 0 && s_screens.Length == 0) RefreshScreens(rescan: false);
        return s_screens.Length > 0 ? s_screens : DisplayScreens();
    }

    /// <summary>Off the main thread, before it has read them: CoreGraphics' displays (its global
    /// coordinates are Win32's), the menu bar taken off the main display's working area.</summary>
    private static unsafe Screen[] DisplayScreens()
    {
        uint* ids = stackalloc uint[16];
        if (CGGetActiveDisplayList(16, ids, out uint count) != 0 || count == 0) return Array.Empty<Screen>();
        uint main = CGMainDisplayID();
        var list = new System.Collections.Generic.List<Screen>();
        NSRect b = CGDisplayBounds(main);
        var bounds = new Rectangle((int)b.x, (int)b.y, (int)b.w, (int)b.h);
        list.Add(new Screen(true, "DISPLAY1", bounds, Rectangle.FromLTRB(bounds.Left, bounds.Top + 25, bounds.Right, bounds.Bottom)));
        for (int i = 0; i < count; i++)
        {
            if (ids[i] == main) continue;
            b = CGDisplayBounds(ids[i]);
            bounds = new Rectangle((int)b.x, (int)b.y, (int)b.w, (int)b.h);
            list.Add(new Screen(false, $"DISPLAY{list.Count + 1}", bounds, bounds));
        }
        return list.ToArray();
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] private static extern unsafe int CGGetActiveDisplayList(uint max, uint* displays, out uint count);
    [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] private static extern NSRect CGDisplayBounds(uint display);

    /// <summary>Re-reads the displays from NSScreen (main thread): at start, then at most once a
    /// second from the pump, which is when a display can have been added, removed or rearranged.</summary>
    private static void RefreshScreens(bool rescan)
    {
        s_screensAt = Environment.TickCount64;
        IntPtr screens = Send(Cls("NSScreen"), Sel("screens"));
        int n = screens == IntPtr.Zero ? 0 : (int)(nint)Send(screens, Sel("count"));
        if (n == 0) return;
        double primaryHeight = SendRectRet(SendPtrNUInt(screens, Sel("objectAtIndex:"), 0), Sel("frame")).h;
        Rectangle Win32(NSRect r) => Rectangle.FromLTRB((int)Math.Round(r.x), (int)Math.Round(primaryHeight - r.y - r.h),
                                                       (int)Math.Round(r.x + r.w), (int)Math.Round(primaryHeight - r.y));
        var list = new Screen[n];
        for (int i = 0; i < n; i++)
        {
            IntPtr screen = SendPtrNUInt(screens, Sel("objectAtIndex:"), (nuint)i);
            list[i] = new Screen(i == 0, $"DISPLAY{i + 1}", Win32(SendRectRet(screen, Sel("frame"))), Win32(SendRectRet(screen, Sel("visibleFrame"))));
        }
        bool same = list.Length == s_screens.Length;
        for (int i = 0; same && i < list.Length; i++)
            same = list[i].Bounds == s_screens[i].Bounds && list[i].WorkingArea == s_screens[i].WorkingArea;
        if (same) return;
        s_screens = list;
        if (rescan) Screen.Rescan();
    }

    /// <summary>Form.WindowState was set: miniaturize, zoom or restore the window. Nothing listened
    /// here (only the Windows host did), so a form's own minimize and maximize buttons did nothing.</summary>
    private static void ApplyWindowState(IntPtr formHandle, FormWindowState state)
    {
        CocoaHost host = null;
        lock (s_live)
            foreach (CocoaHost h in s_live)
                if (!h._form.IsDisposed && h._form.IsHandleCreated && h._form.Handle == formHandle) { host = h; break; }
        if (host == null) return;
        OnMain(() =>
        {
            IntPtr window = host._window;
            if (window == IntPtr.Zero || host._closed) return;
            bool iconic = (nint)Send(window, Sel("isMiniaturized")) != 0, zoomed = (nint)Send(window, Sel("isZoomed")) != 0;
            if (s_traceFrames) Console.WriteLine($"frame: '{host._form.Text}' state {state} requested; iconic={iconic} zoomed={zoomed}");
            if (state == FormWindowState.Minimized && !iconic) SendVoidPtr(window, Sel("miniaturize:"), IntPtr.Zero);
            else if (state == FormWindowState.Maximized)
            {
                if (iconic) SendVoidPtr(window, Sel("deminiaturize:"), IntPtr.Zero);
                if (!zoomed) SendVoidPtr(window, Sel("zoom:"), IntPtr.Zero);
            }
            else if (state == FormWindowState.Normal)
            {
                if (iconic) SendVoidPtr(window, Sel("deminiaturize:"), IntPtr.Zero);
                else if (zoomed) SendVoidPtr(window, Sel("zoom:"), IntPtr.Zero);
            }
        });
    }

    // The window's state, as the driver knows it: Win32Host reports it from each WM_SIZE. Never reported
    // here, the driver kept the state the application last SET -- Minimized, after its own minimize
    // button -- once the window was back from the Dock, and the form went on as a minimized one.
    private FormWindowState _reportedState = FormWindowState.Normal;

    private static readonly bool s_traceFrames = Environment.GetEnvironmentVariable("WF_TRACE_FRAMES") == "1";

    private void ReportWindowState()
    {
        if (_window == IntPtr.Zero || _form.IsDisposed || !_form.IsHandleCreated) return;
        FormWindowState state = (nint)Send(_window, Sel("isMiniaturized")) != 0 ? FormWindowState.Minimized
            : (nint)Send(_window, Sel("isZoomed")) != 0 ? FormWindowState.Maximized : FormWindowState.Normal;
        if (state == _reportedState) return;
        _reportedState = state;
        (_driver as XplatUIWebGpu)?.HostWindowState(_form.Handle, state);
    }

    private void SyncFrame()
    {
        if (_window == IntPtr.Zero || _form.IsDisposed) return;
        ReportWindowState();
        // A minimized window keeps its frame; the form's restore bounds are not this.
        if ((nint)Send(_window, Sel("isMiniaturized")) != 0) return;

        Rectangle native = NativeBounds();
        if (native.Width <= 0 || native.Height <= 0) return;
        if (s_traceFrames && (native != _knownBounds || _form.Bounds != _knownBounds))
            Console.WriteLine($"frame: '{_form.Text}' native={native} known={_knownBounds} form={_form.Bounds} zoomed={(nint)Send(_window, Sel("isZoomed")) != 0}");
        if (native != _knownBounds)
        {
            // Moved or resized by the user (or by AppKit placing it, or zooming it): the form follows,
            // as a Win32 window the system moved does -- the window's position changes and the form
            // hears WM_WINDOWPOSCHANGED (UpdateBounds). Not through Form.Bounds, which is the
            // application moving its own window (SetBoundsCore): DotNetBar's ribbon form takes the
            // Win32 frame off a size set that way while maximized, so a zoomed window was set back
            // 16 x 39 smaller than the screen, and no longer zoomed, it could not be restored.
            _knownBounds = native;
            if (_form.Bounds != native) XplatUI.SetWindowPos(_form.Handle, native.X, native.Y, native.Width, native.Height);
            return;
        }

        Rectangle managed = _form.Bounds;
        if (managed != _knownBounds && managed.Width > 0 && managed.Height > 0)
        {
            // Moved or resized from code: the window follows.
            _knownBounds = managed;
            NSRect content = CocoaRect(managed.Left, managed.Top, managed.Width, managed.Height);
            NSRect frame = SendRectRect(_window, Sel("frameRectForContentRect:"), content);
            SendVoidRectBool(_window, Sel("setFrame:display:"), frame, true);
        }
    }

    /// <summary>The window's content rectangle in the form's coordinates (top-left origin, points).</summary>
    private Rectangle NativeBounds()
    {
        NSRect frame = SendRectRet(_window, Sel("frame"));
        NSRect content = SendRectRect(_window, Sel("contentRectForFrameRect:"), frame);
        double screenHeight = PrimaryScreenHeight();
        return new Rectangle((int)Math.Round(content.x), (int)Math.Round(screenHeight - content.y - content.h),
                             (int)Math.Round(content.w), (int)Math.Round(content.h));
    }

    // Screen coordinates are anchored to the PRIMARY screen (the one with the menu bar), on both
    // sides: Win32's origin is its top-left, Cocoa's its bottom-left. (NSScreen.mainScreen is the
    // screen of the key window, which moves.)
    private static double PrimaryScreenHeight()
    {
        IntPtr screens = Send(Cls("NSScreen"), Sel("screens"));
        IntPtr primary = screens != IntPtr.Zero && (nint)Send(screens, Sel("count")) > 0
            ? SendPtrNUInt(screens, Sel("objectAtIndex:"), 0)
            : Send(Cls("NSScreen"), Sel("mainScreen"));
        return primary == IntPtr.Zero ? 900 : SendRectRet(primary, Sel("frame")).h;
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRect(IntPtr r, IntPtr s, NSRect a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRectBool(IntPtr r, IntPtr s, NSRect a, [MarshalAs(UnmanagedType.I1)] bool b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNUInt(IntPtr r, IntPtr s, nuint a);

    /// <summary>
    /// Where the form asked to be (its Location, which StartPosition has already resolved), in
    /// Cocoa's coordinates: origin at the bottom-left of the main screen, measured to the window's
    /// bottom edge. Main thread only (NSScreen).
    /// </summary>
    private static NSRect CocoaRect(int left, int top, double w, double h)
    {
        double screenHeight = PrimaryScreenHeight();
        return new NSRect { x = left, y = screenHeight - top - h, w = w, h = h };
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRet(IntPtr r, IntPtr s);

    // ---- the main queue -------------------------------------------------------------

    private static IntPtr s_mainQueue;

    // Work for the main thread is queued twice: to the main dispatch queue, which runs it when the
    // main thread next services its run loop, and here, where the main thread picks it up at the
    // next call it makes into the driver (DrainMainWork). Whichever comes first runs it. The second
    // is for a main thread that is busy: an app that shows a splash screen from a thread of its own
    // goes on loading on the main thread, which may not service its run loop for seconds. The
    // splash's window and surface waited for it the whole time, and the splash was an empty window
    // until the moment it closed.
    private sealed class MainWork
    {
        private Action _action;
        public MainWork(Action action) => _action = action;

        public void Run()
        {
            Action action = Interlocked.Exchange(ref _action, null);
            if (action == null) return;
            try { action(); }
            catch (Exception e) { Console.Error.WriteLine($"[CocoaHost] main-queue work failed: {e}"); }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<MainWork> s_mainWork = new();

    /// <summary>Runs <paramref name="action"/> on the main thread: now if this is it, else queued.</summary>
    private static unsafe void OnMain(Action action)
    {
        if (pthread_main_np() != 0) { action(); return; }
        if (s_mainQueue == IntPtr.Zero)
        {
            // dispatch_get_main_queue() is a macro for &_dispatch_main_q.
            s_mainQueue = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.dylib"), "_dispatch_main_q");
        }
        var work = new MainWork(action);
        s_mainWork.Enqueue(work);
        GCHandle handle = GCHandle.Alloc(work);
        dispatch_async_f(s_mainQueue, GCHandle.ToIntPtr(handle), (IntPtr)(delegate* unmanaged<IntPtr, void>)&RunQueued);
    }

    /// <summary>On the main thread, runs the work other threads have queued for it (see MainWork).
    /// The driver calls this from the calls a busy main thread keeps making.</summary>
    internal static void DrainMainWork()
    {
        if (s_mainWork.IsEmpty || pthread_main_np() == 0) return;
        while (s_mainWork.TryDequeue(out MainWork work)) work.Run();
    }

    [UnmanagedCallersOnly]
    private static void RunQueued(IntPtr context)
    {
        GCHandle handle = GCHandle.FromIntPtr(context);
        var work = (MainWork)handle.Target;
        handle.Free();
        work.Run();
    }

    [DllImport("/usr/lib/libSystem.dylib")] private static extern int pthread_main_np();
    [DllImport("/usr/lib/libSystem.dylib")] private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);

    // Save an RGBA byte buffer (WgpuSceneRenderer readback layout: R,G,B,A) to a PNG via System.Drawing.
    internal static void SaveRgbaPng(byte[] rgba, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* dst = (byte*)bd.Scan0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4, d = y * bd.Stride + x * 4;
                        dst[d + 0] = rgba[s + 2]; // B
                        dst[d + 1] = rgba[s + 1]; // G
                        dst[d + 2] = rgba[s + 0]; // R
                        dst[d + 3] = rgba[s + 3]; // A
                    }
            }
        }
        finally { bmp.UnlockBits(bd); }
        bmp.Save(path, ImageFormat.Png);
    }

    private readonly MethodInfo _getCaret;
    private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew();

    // Overlay the blinking text caret onto the composite (the backing bitmaps don't contain it).
    private void DrawCaret(Bitmap dest)
    {
        if (_getCaret == null) return;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return;
        if ((BlinkMs() / 530) % 2 != 0) return;   // ~530ms blink
        int x = (int)a[0], y = (int)a[1], w = Math.Max(1, (int)a[2]), h = (int)a[3];
        using var g = Graphics.FromImage(dest);
        using var b = new SolidBrush(Color.Black);
        g.FillRectangle(b, x, y, w, h);
    }

    /// <summary>Inject a click at a WinForms screen point (as a real NSEvent click would route).</summary>
    public void InjectClickScreen(int x, int y) => _injectClick.Invoke(_driver, new object[] { x, y });

    /// <summary>Save the current composited frame (what's on screen) to a PNG for verification.</summary>
    public void SaveFrame(string path)
    {
        if (_wgpu != null && _gpuRaster)
        {
            // What is on screen is the recorded scene; the back buffers are empty in this mode.
            var scenes = GetScenes(out int ox, out int oy);
            byte[] rgba = _wgpu.RenderScenesToRgba(scenes, GetCaretRect(ox, oy));
            SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, path);
            return;
        }
        using Bitmap b = Composite();
        b.Save(path, ImageFormat.Png);
    }

    // Drain pending NSEvents; route mouse messages to the driver as SEPARATE down/up/move so
    // WinForms' pressed/hover repaints happen between frames. Non-blocking (nil-date poll).
    /// <summary>Take the window down because the form closed itself (an OK button rather than the
    /// window close box). Idempotent.</summary>
    public void Close()
    {
        _form.VisibleChanged -= OnFormVisibleChanged;
        if (_offMain)
        {
            _closed = true;
            OnMain(() =>
            {
                IntPtr w = _window;
                _window = IntPtr.Zero;
                _imageView = IntPtr.Zero;
                if (w != IntPtr.Zero) Send(w, Sel("close"));
            });
            return;
        }
        if (_window == IntPtr.Zero) return;
        StopObservingLiveResize();
        s_live.Remove(this);
        ClosePopupLayer();
        IntPtr win = _window;
        _window = IntPtr.Zero;
        Send(win, Sel("close"));
    }

    public bool Pump()
    {
        // Events are the main thread's to read; see _offMain.
        if (_offMain) return !_closed && _form.Visible && !_form.IsDisposed;

        HookWpfIfLoaded();
        if (Environment.TickCount64 - s_screensAt > 1000) RefreshScreens(rescan: true);
        SyncFrame();
        ReportActivation();

        // Inside a resize callback (an app's Resize handler calling DoEvents) the queue is AppKit's
        // tracking loop's: see CocoaWindow.PumpEventsCore.
        while (s_inResize == 0)
        {
            IntPtr evt = SendNextEvent(App(), Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                unchecked((nuint)ulong.MaxValue), IntPtr.Zero, NSStr("kCFRunLoopDefaultMode"), true);
            if (evt == IntPtr.Zero) break;
            Route(evt);
        }
        // A miniaturized window is not visible to AppKit, but it is no less the form's: the form is
        // minimized, as it is on Windows while its window sits in the taskbar. Treated as closed, the
        // host went away the moment the window reached the Dock, and the still-visible form was given a
        // new window at once -- out of the Dock, with a native title bar over the form's own caption.
        return (nint)Send(_window, Sel("isVisible")) != 0 || (nint)Send(_window, Sel("isMiniaturized")) != 0;
    }

    // ---- one event queue, several owners -------------------------------------------------
    //
    // AppKit has ONE event queue per process. Every host's Pump reads it, and so does WPF's
    // dispatcher when the app also has WPF windows (an ElementHost, a WindowsFormsHost). Each event
    // goes to the owner of the window it is for, whichever pump dequeued it: a WinForms host's
    // window to that host, a WPF window to WPF, anything else to AppKit. Before, every pump treated
    // every event as its own: a click on the form taken off the queue by WPF's pump went straight to
    // AppKit and the app ignored the mouse, and a click on a WPF window taken by this pump was
    // injected into WinForms at the wrong coordinates (and a second form's host did the same).

    private static readonly System.Collections.Generic.List<CocoaHost> s_live = new();
    private static Action<IntPtr> s_wpfDispatch;
    private static volatile bool s_wpfLoaded;

    static CocoaHost()
    {
        // Form.WindowState set by the application (DotNetBar's own caption buttons post SC_MINIMIZE,
        // which the driver makes Minimized): the real window follows, as ShowWindow does on Windows.
        System.Windows.Forms.XplatUIWebGpu.WindowStateRequested ??= ApplyWindowState;
        // for long work the main thread does outside WinForms (rewriting an assembly as it loads): it can
        // keep the work other threads queue for it moving (see MainWork)
        if (AppContext.GetData("WinFormsWebGpu.DrainMainWork") == null) AppContext.SetData("WinFormsWebGpu.DrainMainWork", (Action)DrainMainWork);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { if (e.LoadedAssembly.GetName().Name == "WindowsBase") s_wpfLoaded = true; };
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == "WindowsBase") s_wpfLoaded = true;
    }

    // On the main thread (WPF's window class must not be initialised elsewhere): once WPF is in the
    // process, take our windows' events from its pump and give it its own from ours.
    private static void HookWpfIfLoaded()
    {
        if (!s_wpfLoaded || s_wpfDispatch != null) return;
        Assembly windowsBase = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WindowsBase");
        Type t = windowsBase?.GetType("MS.Internal.Interop.CocoaWindow");
        FieldInfo hook = t?.GetField("ForeignEventHandler", BindingFlags.Public | BindingFlags.Static);
        MethodInfo dispatch = t?.GetMethod("DispatchEvent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
        if (hook == null || dispatch == null) { s_wpfLoaded = false; return; }
        hook.SetValue(null, (Func<IntPtr, bool>)RouteFromWpf);
        s_wpfDispatch = (Action<IntPtr>)dispatch.CreateDelegate(typeof(Action<IntPtr>));
    }

    private static CocoaHost HostFor(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return null;
        foreach (CocoaHost h in s_live) if (h._window == nsWindow || (h._popupWindow == nsWindow && h._popupShown)) return h;
        return null;
    }

    /// <summary>
    /// The host a mouse move AppKit attributed to no window belongs to: the one whose popup layer is
    /// showing (a menu is above everything of its form), else the one whose window is under the pointer.
    /// AppKit sends a bare move with no window when the pointer is over none of the KEY window's own
    /// views -- with an ElementHost's window key, over the form's popup layer -- and such moves were
    /// dropped, so a menu opened over the canvas highlighted nothing until it was clicked.
    /// </summary>
    private static CocoaHost HostUnderPointer(IntPtr evt)
    {
        if (Send(evt, Sel("window")) != IntPtr.Zero) return null;
        nint type = (nint)Send(evt, Sel("type"));
        if (type != 5 && type != 6 && type != 7) return null;
        NSPoint p = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
        static bool Inside(NSRect r, NSPoint q) => q.x >= r.x && q.x < r.x + r.w && q.y >= r.y && q.y < r.y + r.h;
        foreach (CocoaHost h in s_live) if (h._popupShown && h._window != IntPtr.Zero && Inside(h._popupFrame, p)) return h;
        foreach (CocoaHost h in s_live) if (h._window != IntPtr.Zero && Inside(SendRectRet(h._window, Sel("frame")), p)) return h;
        return null;
    }

    private static bool RouteFromWpf(IntPtr evt)
    {
        CocoaHost host = HostFor(Send(evt, Sel("window"))) ?? HostUnderPointer(evt);
        if (host == null) return FilterEmbeddedPress(evt);
        host.HandleEvent(evt);
        return true;
    }

    private static void Route(IntPtr evt)
    {
        CocoaHost host = HostFor(Send(evt, Sel("window"))) ?? HostUnderPointer(evt);
        if (host != null) host.HandleEvent(evt);
        else if (FilterEmbeddedPress(evt)) return;
        else if (s_wpfDispatch != null) s_wpfDispatch(evt);
        else Send(App(), Sel("sendEvent:"), evt);
    }

    /// <summary>Where the pointer is, in Win32's screen coordinates (top-left of the primary
    /// screen), wherever it is: over the form, an embedded window, or none of the app's.</summary>
    internal static bool TryRealCursor(out int x, out int y)
    {
        x = y = 0;
        try
        {
            NSPoint p = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
            x = (int)Math.Floor(p.x);
            y = (int)Math.Floor(PrimaryScreenHeight() - p.y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // A press on a WPF window embedded in a form (an ElementHost's child window). On Windows it is
    // a message of the thread the WinForms loop pumps, so the application's message filters see it
    // before WPF does: DotNetBar closes an open popup from its filter on any press outside it. Here
    // it reached WPF alone -- through the popup layer, whose see-through parts let the click fall to
    // the window below -- and app's Controls gallery stayed open over a click on the canvas.
    // True when a filter took the message, which then does not reach WPF.
    private static bool FilterEmbeddedPress(IntPtr evt)
    {
        try
        {
            nint type = (nint)Send(evt, Sel("type"));
            if (type != 1 && type != 3) return false;   // left or right mouse down
            IntPtr window = Send(evt, Sel("window"));
            CocoaHost host = window == IntPtr.Zero ? null : HostFor(Send(window, Sel("parentWindow")));
            if (host == null || host._form.IsDisposed || !host._form.IsHandleCreated) return false;
            // The pointer in screen coordinates (Win32's: top-left of the primary screen).
            NSPoint loc = SendPointRet(evt, Sel("locationInWindow"));
            NSRect frame = SendRectRet(window, Sel("frame"));
            var screen = new System.Drawing.Point((int)(frame.x + loc.x), (int)(PrimaryScreenHeight() - (frame.y + loc.y)));
            (host._driver as XplatUIWebGpu)?.NotePointer(screen.X, screen.Y);
            Control target = host._form;
            while (target.GetChildAtPoint(target.PointToClient(screen), GetChildAtPointSkip.Invisible) is Control child) target = child;
            System.Drawing.Point client = target.PointToClient(screen);
            Message m = Message.Create(target.Handle, type == 1 ? (int)Msg.WM_LBUTTONDOWN : (int)Msg.WM_RBUTTONDOWN,
                                       (IntPtr)(type == 1 ? 1 : 2), (IntPtr)((client.Y << 16) | (client.X & 0xffff)));   // MK_LBUTTON / MK_RBUTTON
            bool taken = Application.FilterMessage(ref m);
            if (s_traceInput) Console.WriteLine($"[cocoa input] embedded press at {screen} -> {target.GetType().Name} '{target.Name}' client {client}: filtered={taken}");
            return taken;
        }
        catch
        {
            return false;
        }
    }

    // ---- the wheel ----------------------------------------------------------------------
    //
    // Nothing turned a scroll into WM_MOUSEWHEEL here, so no list, grid or text box scrolled with
    // the wheel or the trackpad. Windows measures the wheel in notches of 120, and controls divide
    // by that (the property grid scrolls delta / 120 rows), so the fine-grained deltas of a trackpad
    // or of an accelerated mouse are added up and handed on a notch at a time.
    private const double PointsPerNotch = 30;   // a trackpad's travel for one wheel notch
    private double _wheelUnits;

    private void HandleScrollWheel(IntPtr evt)
    {
        double dy = SendDouble(evt, Sel("scrollingDeltaY"));
        bool precise = Send(evt, Sel("hasPreciseScrollingDeltas")) != IntPtr.Zero;
        // Both are what the user's scroll direction setting makes of the gesture, as Windows'
        // delta is: positive scrolls the content up into view, as the wheel turned away does.
        _wheelUnits += precise ? dy * 120 / PointsPerNotch : dy * 120;
        int notches = (int)(_wheelUnits / 120);
        if (notches == 0) return;
        _wheelUnits -= notches * 120;

        NSPoint loc = LocationInContent(evt, out _);
        int cx = (int)loc.x + _form.Left;
        int cy = (int)(_form.Height - loc.y) + _form.Top;
        _wheelInject ??= _driver.GetType().GetMethod("InjectWheel", BindingFlags.NonPublic | BindingFlags.Instance);
        _wheelInject?.Invoke(_driver, new object[] { cx, cy, notches * 120 });
        Application.DoEvents();
        Present();
    }

    private MethodInfo _wheelInject;

    // ---- sizing from the form's own border ------------------------------------------------
    //
    // Windows' SC_SIZE loop: the window's frame follows the pointer from the press on the border to
    // the release, within the form's minimum and maximum size. Each change of frame reaches the form
    // through the resize notification (OnWindowResized), as a live resize does.

    private int _sizingHit;
    private NSRect _sizingFrame;
    private NSPoint _sizingStart;

    private bool IsResizable() => _form.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow
                                  && _form.WindowState == FormWindowState.Normal;

    private NSPoint ScreenPoint(NSPoint inWindow)
    {
        NSRect frame = SendRectRet(_window, Sel("frame"));
        return new NSPoint { x = frame.x + inWindow.x, y = frame.y + inWindow.y };
    }

    private void BeginSizing(int hit, NSPoint loc)
    {
        _sizingHit = hit;
        _sizingFrame = SendRectRet(_window, Sel("frame"));
        _sizingStart = ScreenPoint(loc);
    }

    /// <summary>A mouse event during sizing: the frame follows a drag, a release ends it. True when
    /// the event was the sizing's.</summary>
    private bool ContinueSizing(nint type, NSPoint loc)
    {
        if (type == 2) { _sizingHit = 0; return true; }
        if (type != 6) return type == 5 || type == 1;
        NSPoint p = ScreenPoint(loc);
        double dx = p.x - _sizingStart.x, dy = p.y - _sizingStart.y;   // Cocoa: y grows upwards
        NSRect f = _sizingFrame;
        bool left = _sizingHit is HTLEFT or HTTOPLEFT or HTBOTTOMLEFT;
        bool right = _sizingHit is HTRIGHT or HTTOPRIGHT or HTBOTTOMRIGHT;
        bool top = _sizingHit is HTTOP or HTTOPLEFT or HTTOPRIGHT;
        bool bottom = _sizingHit is HTBOTTOM or HTBOTTOMLEFT or HTBOTTOMRIGHT;
        double w = f.w + (right ? dx : left ? -dx : 0);
        double h = f.h + (top ? dy : bottom ? -dy : 0);
        // The form's limits, else Windows' smallest tracking size (SM_CXMINTRACK x SM_CYMINTRACK).
        System.Drawing.Size min = _form.MinimumSize, max = _form.MaximumSize;
        w = Math.Max(w, min.Width > 0 ? min.Width : 136);
        h = Math.Max(h, min.Height > 0 ? min.Height : 39);
        if (max.Width > 0) w = Math.Min(w, max.Width);
        if (max.Height > 0) h = Math.Min(h, max.Height);
        NSRect next = new NSRect { x = left ? f.x + f.w - w : f.x, y = bottom ? f.y + f.h - h : f.y, w = w, h = h };
        SendVoidRectBool(_window, Sel("setFrame:display:"), next, true);
        return true;
    }

    // The event being handled, for a window move the form starts from inside it (SC_MOVE).
    [ThreadStatic] private static IntPtr t_event;
    [ThreadStatic] private static CocoaHost t_eventHost;

    private static void MoveWindowWithPointer(IntPtr handle)
    {
        CocoaHost host = t_eventHost;
        if (host == null || t_event == IntPtr.Zero || host._window == IntPtr.Zero) return;
        if (!host._form.IsHandleCreated || host._form.Handle != handle) return;
        host._formHasMouse = false;   // the window drag takes the rest of this press
        SendVoidPtr(host._window, Sel("performWindowDragWithEvent:"), t_event);
    }

    private void HandleEvent(IntPtr evt)
    {
        XplatUIWebGpu.BeginWindowMove ??= MoveWindowWithPointer;
        IntPtr outerEvent = t_event;
        CocoaHost outerHost = t_eventHost;
        IntPtr outerRoot = XplatUIWebGpu.InputRoot;
        t_event = evt;
        t_eventHost = this;
        XplatUIWebGpu.InputRoot = _form.IsHandleCreated ? _form.Handle : IntPtr.Zero;
        try { HandleEventCore(evt); }
        finally { t_event = outerEvent; t_eventHost = outerHost; XplatUIWebGpu.InputRoot = outerRoot; }
    }

    private void HandleEventCore(IntPtr evt)
    {
        nint type = (nint)Send(evt, Sel("type"));
        // NSEventType: LeftMouseDown=1, LeftMouseUp=2, RightMouseDown=3, RightMouseUp=4,
        // MouseMoved=5, LeftMouseDragged=6.
        if (type >= 1 && type <= 6)
        {
            NSPoint loc = LocationInContent(evt, out bool viaLayer);
            if (_sizingHit != 0 && ContinueSizing(type, loc)) return;
            // A press on a sizing border the form draws itself sizes the window, as Windows' default
            // handling of HTLEFT..HTBOTTOMRIGHT does. AppKit sizes only from its own frame: its title
            // bar zone at the top, nothing along the other edges, which the content covers.
            if (type == 1 && _ownCaption && !_formHasMouse && !viaLayer && IsResizable())
            {
                int sizing = HitTest((int)loc.x + _form.Left, (int)(_form.Height - loc.y) + _form.Top);
                if (sizing >= HTLEFT && sizing <= HTBOTTOMRIGHT)
                {
                    BeginSizing(sizing, loc);
                    return;
                }
            }
            // A press, drag or release in the content is the form's alone. Handed on to AppKit as
            // well, it ran AppKit's own tracking on it -- and a drag of a splitter inside the form
            // resized and moved the whole window, taking an ElementHost's window off the form. The
            // title bar and a band along the edges stay AppKit's, so the window still moves and
            // resizes the native way; those are not the form's. Bare moves go to both (cursor).
            const double EdgeBand = 4;
            bool inContent = loc.y >= 0 && loc.y < _form.Height && loc.x >= 0 && loc.x < _form.Width;
            bool atEdge = loc.x < EdgeBand || loc.x >= _form.Width - EdgeBand || loc.y < EdgeBand;
            // Through the popup layer it is a popup's, wherever it is: the layer lets the pointer
            // through where it shows nothing.
            if (type != 5 && (!inContent || atEdge) && !_formHasMouse && !viaLayer)
            {
                Send(App(), Sel("sendEvent:"), evt);
                return;
            }
            // A press on a caption the form draws itself moves the window, and a double click zooms
            // it, as Windows' default handling of HTCAPTION does; a press on its border resizes it.
            if (type == 1 && _ownCaption && !_formHasMouse)
            {
                int hit = HitTest((int)loc.x + _form.Left, (int)(_form.Height - loc.y) + _form.Top);
                if (hit == HTCAPTION)
                {
                    if ((nint)Send(evt, Sel("clickCount")) >= 2) SendVoidPtr(_window, Sel("zoom:"), IntPtr.Zero);
                    else SendVoidPtr(_window, Sel("performWindowDragWithEvent:"), evt);
                    return;
                }
                if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT)
                {
                    Send(App(), Sel("sendEvent:"), evt);
                    return;
                }
            }
            // A press on the form's own content takes the keyboard back from a child window of the form
            // that has it (an ElementHost's WPF window, see CocoaWindow.TakesKeyboardOnPress): on Windows
            // the clicked control takes the focus. Not through the popup layer: a menu takes no focus.
            if ((type == 1 || type == 3) && !viaLayer && Send(App(), Sel("keyWindow")) != _window)
                SendVoidPtr(_window, Sel("makeKeyWindow"), IntPtr.Zero);
            if (type == 1 || type == 3) _formHasMouse = true;
            else if (type == 2 || type == 4) _formHasMouse = false;
            // Window point (Cocoa: bottom-left origin) to the driver's screen space, where it
            // hit-tests: the form's client area is drawn at the form's location there. Window
            // coordinates alone were right only while every form sat at (0,0) -- which stopped
            // being true when the form began to report where the window really is.
            int cx = (int)loc.x + _form.Left;
            int cy = (int)(_form.Height - loc.y) + _form.Top;
            if (s_traceInput) Console.WriteLine($"[cocoa input] type={type} window=({loc.x},{loc.y}) -> driver=({cx},{cy}) form={_form.Bounds}");
            switch (type)
            {
                case 1: _down.Invoke(_driver, new object[] { cx, cy }); break;
                case 2: _up.Invoke(_driver, new object[] { cx, cy }); break;
                case 3: _rightDown.Invoke(_driver, new object[] { cx, cy }); break;
                case 4: _rightUp.Invoke(_driver, new object[] { cx, cy }); break;
                case 5: _move.Invoke(_driver, new object[] { cx, cy, false }); break;
                case 6: _move.Invoke(_driver, new object[] { cx, cy, true }); break;
            }
            Application.DoEvents();   // let the control repaint (pressed/hover) into its backbuffer
            Present();                // show it immediately
            if (type == 5) Send(App(), Sel("sendEvent:"), evt);
            return;
        }
        else if (type == 22) // NSEventTypeScrollWheel -> WM_MOUSEWHEEL at the pointer
        {
            HandleScrollWheel(evt);
            return;
        }
        else if (type == 10) // NSEventTypeKeyDown -> route to the focused control
        {
            IntPtr chars = Send(evt, Sel("characters"));
            string s = chars == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(chars, Sel("UTF8String"))) ?? "";
            foreach (char c in s)
            {
                // Arrow/function keys arrive as the 0xF700+ Unicode private range -> map to VKs;
                // everything else (printable, backspace 0x7F, enter 0x0D) goes as WM_CHAR.
                // macOS function-key private range -> Win32 VK: arrows, Home/End, PageUp/Down,
                // forward Delete. (VK: Left37 Up38 Right39 Down40 End35 Home36 PgUp33 PgDn34 Del46)
                int vk = c switch
                {
                    (char)0xF700 => 38, (char)0xF701 => 40, (char)0xF702 => 37, (char)0xF703 => 39,
                    (char)0xF729 => 36, (char)0xF72B => 35, (char)0xF72C => 33, (char)0xF72D => 34,
                    (char)0xF728 => 46,
                    _ => 0
                };
                if (vk != 0) _keyDown.Invoke(_driver, new object[] { vk });
                // macOS Delete key yields 0x7F, which WinForms treats as Ctrl+Backspace (delete
                // word); plain backspace is 0x08. Translate so Delete removes one character.
                else _char.Invoke(_driver, new object[] { c == (char)0x7F ? (char)0x08 : c });
            }
            Application.DoEvents();
            Present();
            return;   // don't forward key events to AppKit (we've handled them)
        }
        Send(App(), Sel("sendEvent:"), evt);
    }

    // ---- Objective-C runtime -----------------------------------------------------

    private static bool s_appReady;
    internal static void EnsureApp()
    {
        if (s_appReady) return;
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 2);
        dlopen("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", 2);   // CATransaction
        // A top-level autorelease pool: AppKit operations (image decode, string factories) enqueue
        // autoreleased objects and crash without one.
        Send(Send(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        IntPtr app = App();
        SendVoidNInt(app, Sel("setActivationPolicy:"), 0); // Regular
        Send(app, Sel("finishLaunching"));
        s_appReady = true;
    }
    private static IntPtr App() => Send(Cls("NSApplication"), Sel("sharedApplication"));

    [StructLayout(LayoutKind.Sequential)] private struct NSPoint { public double x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct NSRect { public double x, y, w, h; }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);
    private static IntPtr Cls(string n) => objc_getClass(n);
    private static IntPtr Sel(string n) => sel_registerName(n);
    private static IntPtr NSStr(string s) => SendStr(Cls("NSString"), Sel("stringWithUTF8String:"), s);

    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string p, int m);
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string n);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string n);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr r, IntPtr s, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDataBytes(IntPtr r, IntPtr s, IntPtr bytes, nuint len);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendInitWindow(IntPtr r, IntPtr s, NSRect rect, nuint style, nuint backing, [MarshalAs(UnmanagedType.I1)] bool defer);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendInitRect(IntPtr r, IntPtr s, NSRect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPtrNInt(IntPtr r, IntPtr s, IntPtr a, nint b);

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CG)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] private static extern void CGColorSpaceRelease(IntPtr cs);
    [DllImport(CG)] private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint w, nuint h, nuint bpc, nuint bpr, IntPtr cs, uint info);
    [DllImport(CG)] private static extern IntPtr CGBitmapContextCreateImage(IntPtr ctx);
    [DllImport(CG)] private static extern void CGContextRelease(IntPtr ctx);
    [DllImport(CG)] private static extern void CGImageRelease(IntPtr img);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint SendPointRet(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr r, IntPtr s, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNextEvent(IntPtr r, IntPtr s, nuint mask, IntPtr date, IntPtr mode, [MarshalAs(UnmanagedType.I1)] bool dequeue);
}
