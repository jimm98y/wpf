// Minimal on-screen host for the WinForms port on macOS: an NSWindow whose content is an
// NSImageView showing the driver's composited window bitmap, with a poll loop that drains
// NSEvents and routes left-clicks back through the driver (hit-test + WM_LBUTTON dispatch).
// Objective-C runtime P/Invoke, same style as the WPF fork's CocoaWindow. Presentation is a
// CoreGraphics bitmap for now; a WebGPU-texture path is the later swap.

extern alias WindowsBase;

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed partial class CocoaHost : IWinFormsHost, WinFormsWebGpu.Accessibility.IA11yHostSite
{
    private readonly Form _form;
    private readonly XplatUIWebGpu _driver;
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

    // ---- points and the driver's units -------------------------------------------------------
    //
    // An application that is not per-monitor aware lays out at 96 dpi, and a point is one of its
    // pixels: the driver's units are the window's points, and the presenter scales them to the
    // backing's pixels. A per-monitor-aware one (ScaleHelper.HostSourceDpi 0) lays out in device
    // pixels, as on Windows: a driver unit is a pixel of the window's display, _k of them to a point
    // (the backing scale), and the presenter draws them 1:1; a window that moves to a display with
    // another backing scale is told as Windows tells it, WM_DPICHANGED (FollowBacking). Positions on
    // the screen -- a window's origin, the displays' bounds, the pointer -- are in the main display's
    // pixels (ScreenScale), as Windows' physical coordinates are in its monitors' pixels.
    private double _k = 1;
    // the backing scale the window's layer was last given (0 until the first present)
    private double _layerScale;

    // WF_TEST_BACKING=<scale>@<seconds> (diagnostics): from that many seconds after start, the windows'
    // backing scale reads as <scale> -- a display change (one attached, the window moved to another)
    // without a display to attach.
    private static readonly (double Scale, long At) s_testBacking = ParseTestBacking();
    private static (double, long) ParseTestBacking()
    {
        string v = Environment.GetEnvironmentVariable("WF_TEST_BACKING");
        if (string.IsNullOrEmpty(v)) return (0, 0);
        string[] parts = v.Split('@');
        double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale);
        long.TryParse(parts.Length > 1 ? parts[1] : "0", out long seconds);
        return (scale, Environment.TickCount64 + seconds * 1000);
    }

    private bool _testBackingSeen;

    private static double Backing(IntPtr view)
        => s_testBacking.Scale > 0 && Environment.TickCount64 >= s_testBacking.At ? s_testBacking.Scale
            : Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(view);

    private static bool PerMonitor => ScaleHelper.IsDeviceScaled && ScaleHelper.HostSourceDpi == 0;

    /// <summary>Driver units per point for screen positions; 1 until the application's DPI mode is
    /// settled (asking settles it, which is the application's first control's to do).</summary>
    private static double ScreenScale => !ScaleHelper.IsResolved ? 1
        : PerMonitor ? ScaleHelper.InitialSystemDpi / 96.0 : ScaleHelper.HostSourceDpi / 96.0;

    /// <summary>Driver units per point of a window about to be made: the DPI its form is laid out at.</summary>
    private double InitialUnitsPerPoint()
        => PerMonitor && _form.IsHandleCreated && _driver is XplatUIWebGpu d ? d.WindowDpi(_form.Handle) / 96.0 : ScreenScale;

    /// <summary>The window's backing scale (<paramref name="backing"/>) for a per-monitor application:
    /// the driver learns its window's DPI, and when it is not the one the form is laid out at, the form
    /// is told, as Windows tells a window that moved to another monitor (WM_DPICHANGED with the
    /// window's new rectangle): the same size in points, in the new DPI's pixels.</summary>
    private void FollowBacking(double backing)
    {
        if (!PerMonitor || backing <= 0 || !_form.IsHandleCreated || _form.IsDisposed || _driver is not XplatUIWebGpu d) return;
        int dpi = (int)Math.Round(96 * backing);
        if (Math.Abs(backing - _k) < 0.001)
        {
            if (d.WindowDpi(_form.Handle) != dpi) d.SetWindowDpi(_form.Handle, dpi);
            return;
        }
        NSRect content = ContentScreenRect();
        _k = backing;
        Padding frame = Frame;
        var suggested = new Rectangle(_form.Location, new Size((int)Math.Round(content.w * backing) + frame.Horizontal, (int)Math.Round(content.h * backing) + frame.Vertical));
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' to {dpi} dpi, suggested {suggested}");
        _knownBounds = suggested;
        if (ScaleHelper.IsThreadPerMonitorV2Aware) d.HostDpiChanged(_form.Handle, dpi, suggested);
        else d.SetWindowDpi(_form.Handle, dpi);
        _lastVer = -1;
    }

    internal CocoaHost(Form form)
    {
        _form = form;
        _driver = XplatUIWebGpu.GetInstance();
        // Claim the app's on-screen host slot, so the driver's message loop drives THIS window
        // rather than creating a second one of its own.
        PresentationHost.Attach(this, form);
    }

    // ---- composite the WinForms window tree into one bitmap ----------------------

    private Bitmap Composite()
    {
        Rectangle client = ClientScreen;
        var bmp = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height));
        using var g = Graphics.FromImage(bmp);
        // Walk EVERY visible window by the driver's window tree (not just the form's Control tree),
        // so top-level popups (ComboBox/menu dropdowns are separate WS_POPUP windows) are drawn too.
        // Positions are absolute screen coords; the form is the presentation origin.
        long[] wins = (long[])_driver.GetPresentWindows(_form.Handle);
        // First triple is the form itself -> use it as the origin.
        int ox = wins.Length >= 3 ? (int)wins[1] : 0;
        int oy = wins.Length >= 3 ? (int)wins[2] : 0;
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            var bb = (Bitmap)_driver.GetWindowBackBuffer((IntPtr)wins[i]);
            if (bb != null) g.DrawImageUnscaled(bb, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy);
        }
        return bmp;
    }

    // Build one GPU layer per visible driver window (form + children + WS_POPUP dropdowns), in the
    // driver's paint order, positioned in form space. This is the per-window GPU-compositing input
    // (the GPU draws each control's bitmap as its own quad, instead of a CPU pre-composite).
    private System.Collections.Generic.List<WgpuLayer> GetLayers(out int ox, out int oy)
    {
        long[] wins = (long[])_driver.GetPresentWindows(_form.Handle);
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var layers = new System.Collections.Generic.List<WgpuLayer>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            var bb = (Bitmap)_driver.GetWindowBackBuffer((IntPtr)wins[i]);
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
        long[] wins = (long[])_driver.GetPresentWindows(_form.Handle);
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var list = new System.Collections.Generic.List<(object, int, int)>(wins.Length / 3);
        if (popups != null) _popupRects.Clear();
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _driver.GetWindowScene((IntPtr)wins[i]);
            if (scene == null) continue;
            var item = (scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy);
            if (popups != null && IsPopup((IntPtr)wins[i]))
            {
                if (PopupBelongsHere((IntPtr)wins[i]))
                {
                    popups.Add(item);
                    Hwnd ph = Hwnd.ObjectFromHandle((IntPtr)wins[i]);
                    if (ph != null) _popupRects.Add(new Rectangle(item.Item2, item.Item3, ph.width, ph.height));
                }
            }
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
    // is).
    private long BlinkMs() => _driver.GetCaretBlinkElapsed();

    private Rectangle? GetCaretRect(int ox, int oy)
    {
        if (!_driver.GetCaret(_form.Handle, out int cx, out int cy, out int cw, out int ch)) return null;
        if ((BlinkMs() / 530) % 2 != 0) return null;   // ~530ms blink
        return new Rectangle(cx - ox, cy - oy, Math.Max(1, cw), ch);
    }

    // Caret on/off state only (no position) — the cheap change-check for present-on-change.
    private bool CaretOn()
    {
        if (!_driver.GetCaret(_form.Handle, out _, out _, out _, out _)) return false;
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
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' visible changed: {visible} modal={_form.Modal} window={(_window != IntPtr.Zero)} closed={_closed}");
        OnMain(() =>
        {
            IntPtr window = _window;
            if (window == IntPtr.Zero || _closed) return;
            if (visible)
            {
                ShowWindow(window, activateApp: false);
            }
            else
            {
                HidePopupLayer();
                ReleaseModalParent();
                SendVoidPtr(window, Sel("orderOut:"), IntPtr.Zero);
            }
        });
        if (visible)
        {
            _lastVer = -1;   // it has no picture from while it was away
            Present();
        }
    }

    /// <summary>A hidden form's window is off screen: a modal dialog closed by its title bar's button stayed
    /// up, blank, its form hidden and its loop over, until the button was pressed a second time.</summary>
    public void FormHidden()
    {
        if (_offMain || _window == IntPtr.Zero || _closed) return;
        if ((nint)Send(_window, Sel("isVisible")) == 0) return;
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' hidden, its window was still up: ordered out");
        HidePopupLayer();
        ReleaseModalParent();
        SendVoidPtr(_window, Sel("orderOut:"), IntPtr.Zero);
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

    /// <summary>
    /// Whether the popup <paramref name="handle"/> is in belongs to this form: its owner -- the combo box
    /// a list dropped from, the control a menu opened over, followed through a submenu's parent menu --
    /// is in this form. Every form's layer presented every popup, and the popup layer, which takes the
    /// pointer while it shows one, lies over the whole display: a combo box opened in a modal dialog
    /// put the disabled main window's layer up too, every click fell on it and was dropped as input
    /// for a disabled window, and nothing could be clicked until the application was deactivated. A
    /// popup whose owner is not known is presented by every form that is enabled, as before.
    /// </summary>
    private bool PopupBelongsHere(IntPtr handle)
    {
        IntPtr form = _form.IsHandleCreated ? _form.Handle : IntPtr.Zero;
        IntPtr top = _driver.TopLevelOf(handle);
        for (int depth = 0; depth < 16 && top != IntPtr.Zero; depth++)
        {
            if (!_driver.TryGetOwner(top, out IntPtr owner)) break;
            top = _driver.TopLevelOf(owner);
            if (top == form) return true;
            if (IsHostedForm(top)) return false;   // another form's
        }
        return _form.Enabled;
    }

    private static bool IsHostedForm(IntPtr handle)
    {
        CocoaHost[] live;
        lock (s_live) live = s_live.ToArray();
        foreach (CocoaHost h in live)
            if (h._form.IsHandleCreated && h._form.Handle == handle) return true;
        return false;
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
            ShowPopupLayer(PopupsFrame(ox, oy));
            // The popups are placed against the form; the layer is where they are, in Win32 coordinates
            // its origin is (layer.x, primary height - top).
            double ss = ScreenScale;
            int lx = (int)Math.Round(_popupFrame.x * ss), ly = (int)Math.Round((PrimaryScreenHeight() - _popupFrame.y - _popupFrame.h) * ss);
            var placed = new System.Collections.Generic.List<(object, int, int)>(popups.Count);
            foreach (var (scene, x, y) in popups) placed.Add((scene, x + ox - lx, y + oy - ly));
            if (_popupWgpu != null) _popupWgpu.PresentScenes(placed, null, (int)(_popupFrame.w * _k), (int)(_popupFrame.h * _k));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[CocoaHost] popup layer failed: " + e.Message);
            HidePopupLayer();
        }
    }

    // The popups' rectangles in form space, as GetScenes last found them.
    private readonly System.Collections.Generic.List<Rectangle> _popupRects = new();

    /// <summary>
    /// The layer's frame: just the popups (and their shadows), within the display. The layer takes the
    /// pointer wherever it is; the size of the display, it took every click on the screen while a combo
    /// box's list was open -- another application's window could not be clicked until the list was
    /// closed, where on Windows the click activates that window and the list closes. Over the rest, the
    /// pointer is the form's or the other window's, and a press there closes the popup as Windows' does.
    /// </summary>
    private NSRect PopupsFrame(int ox, int oy)
    {
        NSRect display = PopupLayerFrame();
        if (_popupRects.Count == 0) return display;
        Rectangle u = _popupRects[0];
        foreach (Rectangle r in _popupRects) u = Rectangle.Union(u, r);
        double ss = ScreenScale;
        // form space -> the driver's screen pixels -> Cocoa points (bottom-left origin)
        double x = (u.X + ox) / ss, w = u.Width / ss, h = u.Height / ss;
        double yTop = (u.Y + oy) / ss;
        var frame = new NSRect { x = x, y = PrimaryScreenHeight() - yTop - h, w = w, h = h };
        // whole points, enclosing the popups
        double x0 = Math.Floor(frame.x), y0 = Math.Floor(frame.y);
        frame = new NSRect { x = x0, y = y0, w = Math.Ceiling(frame.x + frame.w) - x0, h = Math.Ceiling(frame.y + frame.h) - y0 };
        return frame.w >= 1 && frame.h >= 1 ? frame : display;
    }

    private void ShowPopupLayer() => ShowPopupLayer(PopupLayerFrame());

    private void ShowPopupLayer(NSRect content)
    {
        if (_popupWindow == IntPtr.Zero)
        {
            _popupWindow = SendInitWindow(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                content, (nuint)0, (nuint)2, false);   // borderless
            SendVoidBool(_popupWindow, Sel("setReleasedWhenClosed:"), false);
            MakeTransparent(_popupWindow);
            _popupView = SendInitRect(Send(Cls("NSView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = content.w, h = content.h });
            SendVoidBool(_popupView, Sel("setWantsLayer:"), true);
            SendVoidPtr(_popupWindow, Sel("setContentView:"), _popupView);
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ProcessGpu.Acquire();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _popupView);
            SendVoidBool(Send(_popupView, Sel("layer")), Sel("setOpaque:"), false);
            double scale = Backing(_popupView);
            _popupWgpu = new WgpuPresenter(ctx, surface, (int)(content.w * _k), (int)(content.h * _k), scale / _k, srgb: _gpuRaster, threadDevice: true);
            _popupWgpu.SetTransparent(true);
            _popupWgpu.Tag = " popups";
            _popupFrame = content;
        }
        // Where the layer really is: a child window moves with its parent, so every drag or zoom of
        // the form since it was placed took it along, off the display, and the popups with it.
        else _popupFrame = SendRectRet(_popupWindow, Sel("frame"));
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
        try { _popupWgpu?.PresentScenes(new System.Collections.Generic.List<(object, int, int)>(), null, (int)(_popupFrame.w * _k), (int)(_popupFrame.h * _k)); }
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
        Rectangle client = ClientScreen;
        int hit = HitTest(client.Left + client.Width / 2, client.Top + 2);
        return hit == HTCAPTION || hit == HTTOP || hit == HTTOPLEFT || hit == HTTOPRIGHT;
    }

    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>The form's own answer to WM_NCHITTEST, whatever is over the point: whether it has taken
    /// the frame over (DrawsOwnCaption). Where a press goes is the driver's FrameHitTest.</summary>
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

    /// <summary>The window shown, as ShowWindow shows a form: active (SW_SHOW), or, for a form that says
    /// ShowWithoutActivation (a notification, a tool tip of its own), in front without taking the keyboard
    /// or the foreground (SW_SHOWNOACTIVATE). Every form was made key, and a notice popping up took the
    /// typing away from whatever the user was in.</summary>
    private void ShowWindow(IntPtr window, bool activateApp)
    {
        if (!_form.ActivateOnShow)
        {
            SendVoidPtr(window, Sel("orderFrontRegardless"), IntPtr.Zero);
            return;
        }
        SendVoidPtr(window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        if (activateApp) Send(App(), Sel("activateIgnoringOtherApps:"), (IntPtr)1);
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

    // ---- the form's region ----------------------------------------------------------------
    //
    // Control.Region on a form is Windows' SetWindowRgn: the window is that shape, nothing of it shows
    // outside, and the desktop shows through. The driver had nowhere to put it, so a shaped form came up
    // as its whole rectangle: a docking guide (a diamond of arrows over the window being docked into) was
    // an opaque grey box. The window is made see-through and its content masked to the region's
    // rectangles, as WPF's window masks an embedded window (CocoaWindow.SetVisibleRegion).

    private string _regionKey;

    private void ApplyFormRegion()
    {
        if (_window == IntPtr.Zero || _imageView == IntPtr.Zero) return;
        Region region = _form.Region;
        RectangleF[] scans = null;
        if (region != null)
        {
            try { using var identity = new System.Drawing.Drawing2D.Matrix(); scans = region.GetRegionScans(identity); }
            catch { scans = null; }
        }
        // the region is the window's, frame and all; the content view is the client area
        Padding frame = Frame;
        var key = new System.Text.StringBuilder(scans == null ? "none" : "");
        if (scans != null)
            foreach (RectangleF r in scans) key.Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append(',').Append(r.Height).Append(';');
        // ...and against the content's size now: masked before the view had its size, every piece lay
        // outside it and the window was masked away entirely -- a docking guide never showed.
        NSRect viewBounds = SendRectRet(_imageView, Sel("bounds"));
        if (scans != null && (viewBounds.w < 1 || viewBounds.h < 1)) return;
        key.Append('|').Append(_k).Append('|').Append(frame).Append('|').Append(viewBounds.w).Append('x').Append(viewBounds.h);
        string k = key.ToString();
        if (k == _regionKey) return;
        _regionKey = k;

        IntPtr layer = Send(_imageView, Sel("layer"));
        if (layer == IntPtr.Zero) { _regionKey = null; return; }
        if (scans == null)
        {
            SendVoidPtr(layer, Sel("setMask:"), IntPtr.Zero);
            return;
        }
        MakeTransparent(_window);
        NSRect bounds = viewBounds;
        IntPtr mask = Send(Cls("CALayer"), Sel("layer"));
        SendVoidRect(mask, Sel("setFrame:"), bounds);
        IntPtr opaque = Send(Send(Cls("NSColor"), Sel("blackColor")), Sel("CGColor"));
        foreach (RectangleF r in scans)
        {
            double x = (r.X - frame.Left) / _k, y = (r.Y - frame.Top) / _k, w = r.Width / _k, h = r.Height / _k;
            IntPtr piece = Send(Cls("CALayer"), Sel("layer"));
            // the content view is not flipped: its layer's origin is the bottom-left
            SendVoidRect(piece, Sel("setFrame:"), new NSRect { x = x, y = bounds.h - y - h, w = w, h = h });
            SendVoidPtr(piece, Sel("setBackgroundColor:"), opaque);
            SendVoidPtr(mask, Sel("addSublayer:"), piece);
        }
        SendVoidPtr(layer, Sel("setMask:"), mask);
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' region of {scans.Length} rectangle(s) applied: frame {frame}, content {bounds.w}x{bounds.h}pt, k={_k}, region bounds {(scans.Length == 0 ? RectangleF.Empty : RectangleF.Union(scans[0], scans[^1]))}, form {_form.Bounds} client {_form.ClientSize} border={_form.FormBorderStyle}");
    }

    // Form.Opacity: Windows' layered window alpha (SetLayeredWindowAttributes, LWA_ALPHA) -- the whole
    // window, frame and content, that much see-through. The driver kept nothing of it, and a docking
    // preview (the half-transparent blue where a dragged panel would go) covered what it marks.
    private double _appliedOpacity = 1.0;

    private void ApplyFormOpacity()
    {
        if (_window == IntPtr.Zero) return;
        double opacity = Math.Clamp(_form.Opacity, 0.0, 1.0);
        if (opacity == _appliedOpacity) return;
        _appliedOpacity = opacity;
        SendVoidDouble(_window, Sel("setAlphaValue:"), opacity);
        if (opacity < 1.0) SendVoidBool(_window, Sel("setOpaque:"), false);
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' opacity {opacity}");
    }

    // Form.TransparencyKey (LWA_COLORKEY): the key's colour see-through. The window has none of that kind, so the
    // presenter keys it out of the picture (WgpuPresenter.WindowColorKey) and the window shows what is behind.
    private System.Drawing.Color _appliedKey = System.Drawing.Color.Empty;

    private void ApplyTransparencyKey()
    {
        if (_window == IntPtr.Zero || _wgpu == null) return;
        System.Drawing.Color key = _form.TransparencyKey;
        if (key == _appliedKey) return;
        _appliedKey = key;
        _wgpu.WindowColorKey = key.IsEmpty ? null : key;
        if (!key.IsEmpty)
        {
            MakeTransparent(_window);
            _wgpu.SetTransparent(true);
        }
        _lastVer = -1;
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
            Rectangle client = ClientScreen;
            double fw = client.Width, fh = client.Height;
            int fx = client.Left, fy = client.Top;
            string title = _form.Text ?? "WinForms";
            _ownCaption = DrawsOwnCaption();
            _form.VisibleChanged += OnFormVisibleChanged;
            nuint style = StyleMask();
            _transparent = IsLayered();
            OnMain(() =>
            {
                if (_closed) return;
                EnsureApp();
                _k = InitialUnitsPerPoint();
                NSRect r = CocoaRect(fx, fy, fw, fh);
                IntPtr nw = SendInitWindow(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                    r, style, (nuint)2, false);
                if (_transparent) MakeTransparent(nw);
                HideTitleBar(nw);
                SendVoidPtr(nw, Sel("setTitle:"), NSStr(title));
                IntPtr nv = SendInitRect(Send(AccessibleViewClass(), Sel("alloc")), Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = r.w, h = r.h });
                SendVoidBool(nv, Sel("setWantsLayer:"), true);
                SendVoidPtr(nw, Sel("setContentView:"), nv);
                _imageView = nv;
                _window = nw;
                EnableDragDrop();
                WinFormsWebGpu.Accessibility.AutomationSource.Register(this);
                // As the main thread's forms do: a process the user just started takes the foreground
                // with its first window. A splash screen shown from its own thread came up before any
                // main-thread form, behind the app it was launched from, and was never seen.
                ShowWindow(nw, activateApp: true);
                if (UseWebGpu)
                {
                    try
                    {
                        // the process's device (ProcessGpu.cs), though the window presents off the main thread
                        var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ProcessGpu.Acquire();
                        IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, nv);
                        _pendingScale = Backing(nv);
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
        Rectangle clientArea = ClientScreen;
        double w = clientArea.Width, h = clientArea.Height;
        IntPtr win = Send(Cls("NSWindow"), Sel("alloc"));
        _k = InitialUnitsPerPoint();
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' shown: k={_k} per-monitor={PerMonitor} handle={_form.IsHandleCreated} windowDpi={_driver.WindowDpi(_form.Handle)} screenScale={ScreenScale} form={_form.Bounds}");
        var rect = CocoaRect(clientArea.Left, clientArea.Top, w, h);
        CenterOnForeignOwner(ref rect);
        _transparent = IsLayered();
        _ownCaption = DrawsOwnCaption();
        win = SendInitWindow(win, Sel("initWithContentRect:styleMask:backing:defer:"), rect, StyleMask(), (nuint)2, false);   // backing Buffered(2)
        if (_transparent) MakeTransparent(win);
        HideTitleBar(win);
        SendVoidPtr(win, Sel("setTitle:"), NSStr(_form.Text ?? "WinForms"));

        IntPtr view = Send(AccessibleViewClass(), Sel("alloc"));
        view = SendInitRect(view, Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = rect.w, h = rect.h });
        SendVoidBool(view, Sel("setWantsLayer:"), true);   // layer-backed: we set layer.contents = CGImage
        SendVoidPtr(win, Sel("setContentView:"), view);

        _window = win; _imageView = view;
        EnableDragDrop();
        if (_form.FormScreenCaptureMode != ScreenCaptureMode.Allow) ApplySharingType(win, _form.FormScreenCaptureMode);
        WinFormsWebGpu.Accessibility.AutomationSource.Register(this);
        // the title bar's close button asks the form, as Windows' does (WindowShouldClose)
        SendVoidPtr(win, Sel("setDelegate:"), CloseDelegate());
        // What the form asked for. AppKit may constrain it (below the menu bar, onto the screen);
        // the first Pump then hands the real frame back to the form.
        _knownBounds = _form.Bounds;
        // the display it came up on: the driver's DPI for the window, and the form's if it is another
        FollowBacking(Backing(_imageView));
        if (s_testBacking.Scale > 0)
        {
            var due = new System.Windows.Forms.Timer { Interval = (int)Math.Max(1, s_testBacking.At - Environment.TickCount64 + 50) };
            due.Tick += (_, _) => { due.Stop(); Console.WriteLine($"frame: simulated backing {Backing(_imageView)} now for '{_form.Text}' presenter={(_wgpu != null)} raster={_gpuRaster} k={_k} closed={_closed}"); Present(); };
            due.Start();
        }
        ObserveLiveResize();
        s_live.Add(this);
        // a hosted WPF frame is something to present: it ends the loop's wait
        Microsoft.Wpf.Interop.WebGpu.Composition.HostedWpfContent.Published = WakeLoop;
        _form.VisibleChanged += OnFormVisibleChanged;
        ShowWindow(win, activateApp: true);
        // the state the form was given before it had a window (an app that restores its placement in Load):
        // only the form's own then (Form.window_state), which Windows makes the window's at creation
        // (WS_MAXIMIZE, WS_MINIMIZE); the driver hears of a state set after (ApplyWindowState)
        if (_form.window_state == FormWindowState.Maximized)
        {
            _restoreFrame = SendRectRet(win, Sel("frame"));
            SendVoidPtr(win, Sel("zoom:"), IntPtr.Zero);
        }
        else if (_form.window_state == FormWindowState.Minimized) SendVoidPtr(win, Sel("miniaturize:"), IntPtr.Zero);

        // WebGPU present path (on unless WF_WEBGPU=0): create a wgpu context, then a CAMetalLayer surface over
        // this NSView (the mac branch of the cross-platform surface abstraction; Windows would use an
        // HWND source, the browser a canvas), then present each composite as a textured quad.
        if (UseWebGpu)
        {
            // the process's device (ProcessGpu.cs), shared with the WPF compositors and the Direct3D shim devices
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ProcessGpu.Acquire();
            // Cross-platform surface creation: NativePlatform dispatches to the CAMetalLayer (mac),
            // HWND (Windows), Xlib (Linux) or canvas (browser) source by detected OS. Here we pass the
            // NSView*; a Win32 host passes its HWND. The present path below is identical everywhere.
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _imageView);
            if (surface == IntPtr.Zero) throw new InvalidOperationException("NativePlatform.CreateWindowSurface returned null");
            // Render at the real backing scale (2x on Retina) so text/geometry are crisp rather than a
            // 1x surface upscaled by the display. The CAMetalLayer's contentsScale (set by CreateSurface)
            // and the surface config must both be device-pixel sized; the presenter scales the scene.
            double scale = Backing(_imageView);
            // Scene path uses an sRGB surface so the renderer's gamma-correct glyph coverage kicks in
            // (crisp, WPF-weight text); the bitmap path stays non-sRGB (pixels are already display-space).
            _wgpu = new WgpuPresenter(ctx, surface, ClientScreen.Width, ClientScreen.Height, scale / _k, srgb: _gpuRaster, threadDevice: true);
            if (_transparent)
            {
                SendVoidBool(Send(_imageView, Sel("layer")), Sel("setOpaque:"), false);
                _wgpu.SetTransparent(true);
            }
            if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
                Console.Error.WriteLine($"WebGPU present path active (surface 0x{surface:x}, format {_wgpu.Format}, scale {scale})");
            // Let embedded non-WinForms content (an ElementHost's WPF tree) reach the real window and
            // its backing scale now that both exist. Inert when nothing is embedded.
            EmbeddedScenes.PublishHostWindow(_imageView, (float)(scale / _k), _form);
        }

        Present();
    }

    public void Present()
    {
        // A form disposed while an event for it was being handled -- a floating panel docked back by
        // the drag that is still in progress -- has nothing to present, and asking it for its handle
        // threw ObjectDisposedException out of the message loop and took the application down.
        if (_form.IsDisposed || _form.Disposing)
            return;
        if (!_offMain) { ApplyFormRegion(); ApplyFormOpacity(); ApplyTransparencyKey(); }
        if (_wgpu != null && _gpuRaster)
        {
            // Runtime DPI detection: re-read the window's backing scale each present; if it changed
            // (e.g. the window moved to a Retina/non-Retina display, or the startup value was stale),
            // resync the CAMetalLayer contentsScale + resize the swap chain so text stays crisp
            // (a wrong 1x on a 2x display would upscale to a pixelated result).
            // A per-monitor application is told of the new DPI and lays itself out for it instead.
            double curScale = Backing(_imageView);
            FollowBacking(curScale);
            // The layer follows the backing, whatever the presenter's scale: for a per-monitor form that
            // is 1 at every DPI (2/2 on Retina, 1/1 after), and keyed on it the layer kept 2 when the
            // window went to a 100% display -- the frame shown in a quarter of the window.
            if (curScale != _layerScale || (float)(curScale / _k) != _wgpu.Scale)
            {
                // the popup layer is made again for the new display (its scale and units are the window's)
                if (_layerScale != 0 && curScale != _layerScale) ClosePopupLayer();
                _layerScale = curScale;
                Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.SetContentsScale(_imageView, curScale);
                _wgpu.SetScale(curScale / _k);
                EmbeddedScenes.PublishHostWindow(_imageView, (float)(curScale / _k), _form);
                _lastVer = -1;   // force a present at the new scale
            }
            // GPU-raster mode: composite each window's RECORDED scene in one pass (no per-control
            // readback / bitmap re-upload). Present ONLY when something changed (driver paint version)
            // or the caret blink toggled — and keep retrying while a present fails (e.g. Occluded until
            // the window is front-most). This makes an idle window cost ~nothing.
            // Fold in the embedded contributors' version too — a hosted WPF tree animates on its own
            // clock and would otherwise never repaint (see EmbeddedScenes).
            int ver = (int)_driver.GetPaintVersion() + EmbeddedScenes.CurrentVersion();
            bool caretOn = CaretOn();
            string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
            bool wantSave = !string.IsNullOrEmpty(save) && !_savedGpu;
            if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk && !wantSave)
                return;   // nothing changed

            var popups = _offMain ? null : new System.Collections.Generic.List<(object, int, int)>();
            var scenes = GetScenes(out int ox, out int oy, popups);
            Rectangle? caret = GetCaretRect(ox, oy);
            // the rubber bands a drag draws (ControlPaint's reversible frames), over the finished frame
            _lastPresentOk = _wgpu.PresentScenes(scenes, caret, _driver.RubberBandsIn(ox, oy), ClientScreen.Width, ClientScreen.Height);
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
            _wgpu.PresentLayers(layers, caret, ClientScreen.Width, ClientScreen.Height);   // one GPU quad per window
            // One-shot: dump the GPU's own output (offscreen readback of the same layered scene).
            string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
            if (!string.IsNullOrEmpty(save) && !_savedGpu)
            {
                _savedGpu = true;
                byte[] rgba = _wgpu.RenderLayersToRgba(layers, caret);
                SaveRgbaPng(rgba, ClientScreen.Width, ClientScreen.Height, save);
                Console.WriteLine($"saved GPU-rendered frame -> {save}");
            }
            return;
        }
        if (_offMain)
        {
            if (_wgpu == null && _pendingSurface != IntPtr.Zero && !_closed)
            {
                _wgpu = new WgpuPresenter(_pendingContext, _pendingSurface, ClientScreen.Width, ClientScreen.Height, _pendingScale / _k, srgb: _gpuRaster, threadDevice: true);
                _wgpu.SetTransparent(_transparent);
                _lastVer = -1;
                if (Environment.GetEnvironmentVariable("WF_TRACE_SURFACE") == "1") Console.WriteLine($"[CocoaHost] off-main presenter for '{_form.Text}' {_form.Width}x{_form.Height}");
            }
            if (_wgpu != null && _gpuRaster && !_closed)
            {
                int v = (int)_driver.GetPaintVersion();
                bool on = CaretOn();
                if (v == _lastVer && on == _lastCaretOn && _lastPresentOk) return;
                var offScenes = GetScenes(out int sx, out int sy);
                Rectangle? offCaret = GetCaretRect(sx, sy);
                _lastPresentOk = _wgpu.PresentScenes(offScenes, offCaret, ClientScreen.Width, ClientScreen.Height);
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
        int ver = (int)_driver.GetPaintVersion();
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
        // the modal size and move loop's ends, WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE (Form.ResizeBegin, ResizeEnd)
        foreach ((string name, IntPtr fn) in new[]
        {
            ("NSWindowWillStartLiveResizeNotification", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnLiveResizeStart),
            ("NSWindowDidEndLiveResizeNotification", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnLiveResizeEnd),
            ("NSWindowWillMoveNotification", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnWillMove),
        })
        {
            IntPtr block = MS.Internal.Interop.ObjCBlock.Create(fn, GCHandle.ToIntPtr(_self));
            if (block == IntPtr.Zero) continue;
            IntPtr observer = SendObserver(center, Sel("addObserverForName:object:queue:usingBlock:"), NSStr(name), _window, IntPtr.Zero, block);
            if (observer != IntPtr.Zero) _sizeMoveObservers.Add(observer);
        }
    }

    // ---- the modal size and move loop -----------------------------------------------------
    //
    // Windows tells a window that the user has begun to move or size it (WM_ENTERSIZEMOVE) and that it is
    // over (WM_EXITSIZEMOVE): Form.ResizeBegin and ResizeEnd, which an application uses to hold off a costly
    // layout until the drag is done. Nothing sent either, and neither event ever came. Here the loop is
    // AppKit's live resize, a drag of the window by its caption (AppKit moves it, and the move is over when
    // the button comes up), or this host's own sizing from a border the form draws.

    private readonly System.Collections.Generic.List<IntPtr> _sizeMoveObservers = new();
    private bool _sizeMoveEntered, _liveResizing, _windowMoving;

    private void SizeMove(bool enter)
    {
        if (enter == _sizeMoveEntered || !_form.IsHandleCreated || _form.IsDisposed) return;
        _sizeMoveEntered = enter;
        _driver.HostSizeMove(_form.Handle, enter);
    }

    /// <summary>From the pump: the move or size is over once nothing of it is going on any more.</summary>
    private void EndSizeMoveIfOver()
    {
        if (!_sizeMoveEntered || _liveResizing || _sizingHit != 0) return;
        if (_windowMoving && RealLeftButtonDown() != false) return;
        _windowMoving = false;
        SizeMove(false);
    }

    private static CocoaHost HostOfBlock(IntPtr block)
    {
        IntPtr context = MS.Internal.Interop.ObjCBlock.ContextOf(block);
        return context == IntPtr.Zero ? null : GCHandle.FromIntPtr(context).Target as CocoaHost;
    }

    [UnmanagedCallersOnly]
    private static void OnLiveResizeStart(IntPtr block, IntPtr notification)
    {
        try { if (HostOfBlock(block) is { } h) { h._liveResizing = true; h.SizeMove(true); } }
        catch (Exception e) { TraceResize("live resize start threw: " + e); }
    }

    [UnmanagedCallersOnly]
    private static void OnLiveResizeEnd(IntPtr block, IntPtr notification)
    {
        try { if (HostOfBlock(block) is { } h) { h._liveResizing = false; h.EndSizeMoveIfOver(); } }
        catch (Exception e) { TraceResize("live resize end threw: " + e); }
    }

    [UnmanagedCallersOnly]
    private static void OnWillMove(IntPtr block, IntPtr notification)
    {
        // the user's drag only: a frame set from code moves the window without a loop
        try { if (HostOfBlock(block) is { } h && RealLeftButtonDown() == true) { h._windowMoving = true; h.SizeMove(true); } }
        catch (Exception e) { TraceResize("will move threw: " + e); }
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
            // inside AppKit's resize loop: a pump WPF runs from here (the embedded tree's frame) must
            // leave AppKit's event queue alone, as this host's does
            bool wpf = s_wpfLoaded;
            if (wpf) EnterWpfAppKitCallback();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t1 = t0, t2 = t0;
            try
            {
                host.SyncFrame();
                // What Windows' modal sizing loop does between moves: the messages the application posted
                // to itself are dispatched (DotNetBar lays its ribbon out from one), not left until the
                // button is let go -- the ribbon stretched only after the drag. AppKit's events stay
                // where they are: the host's pump reads none inside a resize (s_inResize).
                Application.DoEvents();
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                EmbeddedScenes.Pump();
                t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (s_traceResize) Microsoft.Wpf.Interop.WebGpu.Composition.WgpuSceneRenderer.PerfReset();
                // shown with the frame change AppKit is making, not a display refresh after it: a step
                // behind, everything placed relative to the window's size (the caption, centred) jumped
                // back and forth as a maximize animated
                Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacTransactionPresent.Begin();
                try { host.Present(); }
                finally { Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacTransactionPresent.End(); }
            }
            finally
            {
                s_inResize--;
                if (wpf) ExitWpfAppKitCallback();
            }
            if (s_traceResize)
            {
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                TraceResize($"leave native={host.NativeBounds()} form={host._form.Bounds} sync={Ms(t0, t1):F1}ms embedded={Ms(t1, t2):F1}ms present={Ms(t2, t3):F1}ms drew={host._lastPresentOk} layers hit={Microsoft.Wpf.Interop.WebGpu.Composition.WgpuSceneRenderer.PerfLayerHits} miss={Microsoft.Wpf.Interop.WebGpu.Composition.WgpuSceneRenderer.PerfLayerMiss}");
            }
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
            if (center != IntPtr.Zero) foreach (IntPtr o in _sizeMoveObservers) SendVoidPtr(center, Sel("removeObserver:"), o);
            _sizeMoveObservers.Clear();
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

    private void ReportActivation()
    {
        if (_window == IntPtr.Zero || !_form.IsHandleCreated) return;
        // the application's own activation first (WM_ACTIVATEAPP), as Windows sends it before WM_ACTIVATE
        _driver.HostApplicationActive(((nint)Send(App(), Sel("isActive")) & 0xFF) != 0);
        IntPtr key = Send(App(), Sel("keyWindow"));
        bool active = key != IntPtr.Zero && (key == _window || Send(key, Sel("parentWindow")) == _window);
        if (active && !_form.Enabled && ModalHost() is { } modal && modal != this)
        {
            // made key by the Dock, Cmd-Tab or a click AppKit handled itself: on Windows the dialog
            // that disabled it is what activates
            SendVoidPtr(modal._window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            return;
        }
        if (active) s_lastActive = this;
        if (active && !_reportedActive) RaiseOwned();
        if (active == _reportedActive) return;
        _reportedActive = active;
        _driver.HostActivate(_form.Handle.ToInt64(), active);
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
    private static double s_screensScale = 1;

    /// <summary>Called by the driver when it starts: the screens are known before any form places
    /// itself (a splash screen, on a thread of its own, before any window exists).</summary>
    internal static void InstallScreens()
    {
        XplatUIWebGpu.ScreensProvider ??= MacScreens;
        XplatUIWebGpu.SystemBeep ??= NSBeep;
        XplatUIWebGpu.IconChanged ??= ShowFormIcon;
        XplatUIWebGpu.SystrayAdded ??= (h, tip, icon) => { OnMain(() => TrayAdd(h, tip, icon)); return true; };
        XplatUIWebGpu.SystrayChanged ??= (h, tip, icon) => OnMain(() => TrayChange(h, tip, icon));
        XplatUIWebGpu.SystrayRemoved ??= h => OnMain(() => TrayRemove(h));
        XplatUIWebGpu.SystrayBalloonShown ??= MacBalloon;
        XplatUIWebGpu.TrayAtTop = true;   // the menu bar: a balloon drawn by the theme goes under it
        // Caps Lock is the one lock key a Mac has (NSEventModifierFlagCapsLock); its keypad has no Num Lock
        XplatUIWebGpu.KeyLockedProvider ??= vk => vk == 0x14 /* VK_CAPITAL */
            ? (((nuint)(nint)Send(Cls("NSEvent"), Sel("modifierFlags"))) & (1u << 16)) != 0
            : null;
    }

    private static Screen[] MacScreens()
    {
        // again once the DPI mode is settled: a per-monitor application's screens are in pixels
        if (pthread_main_np() != 0 && (s_screens.Length == 0 || s_screensScale != ScreenScale)) RefreshScreens(rescan: s_screens.Length > 0);
        if (s_screens.Length > 0) return s_screens;
        Screen[] guess = DisplayScreens();
        if (guess.Length > 0) s_guessed = guess;
        return guess;
    }

    // What the screens were said to be before the main thread read them (DisplayScreens, which knows
    // the menu bar and not the Dock): what a later reading is compared with, as with one of its own.
    private static Screen[] s_guessed = Array.Empty<Screen>();

    // Broadcasts owed for a change RefreshScreens found, raised from the pump (RaiseScreenChanges):
    // the work area (Windows: WM_SETTINGCHANGE SPI_SETWORKAREA, UserPreferenceChanged with Desktop)
    // and the displays themselves (WM_DISPLAYCHANGE, DisplaySettingsChanged). DotNetBar reads the
    // screens once and again only on UserPreferenceChanged: a drop-down at the Dock took the Dock's
    // band for room it had, and opened behind it instead of upwards.
    private static bool s_workAreaOwed, s_displayOwed;

    private static void RaiseScreenChanges()
    {
        if (!s_workAreaOwed && !s_displayOwed) return;
        bool work = s_workAreaOwed, display = s_displayOwed;
        s_workAreaOwed = s_displayOwed = false;
        if (s_traceFrames) Console.WriteLine($"screens: changed (work area {work}, displays {display}): {string.Join(", ", s_screens.Select(sc => $"{sc.Bounds} work {sc.WorkingArea}"))}");
        // the app's SystemEvents (the stack's own off Windows), which this assembly does not reference
        Type events = Type.GetType("Microsoft.Win32.SystemEvents, Microsoft.Win32.SystemEvents");
        if (events == null) return;
        try
        {
            if (display)
                events.GetMethod("RaiseDisplaySettingsChanged", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            if (work && events.GetMethod("RaiseUserPreference", BindingFlags.NonPublic | BindingFlags.Static) is { } raise)
                raise.Invoke(null, new object[] { Enum.ToObject(raise.GetParameters()[0].ParameterType, 3) });   // Desktop
        }
        catch (TargetInvocationException e) { Console.Error.WriteLine("[CocoaHost] screen change broadcast: " + e.InnerException?.Message); }
    }

    /// <summary>Off the main thread, before it has read them: CoreGraphics' displays (its global
    /// coordinates are Win32's), the menu bar taken off the main display's working area.</summary>
    private static unsafe Screen[] DisplayScreens()
    {
        uint* ids = stackalloc uint[16];
        if (CGGetActiveDisplayList(16, ids, out uint count) != 0 || count == 0) return Array.Empty<Screen>();
        uint main = CGMainDisplayID();
        var list = new System.Collections.Generic.List<Screen>();
        double ss = ScreenScale;
        Rectangle Scaled(NSRect r) => Rectangle.FromLTRB((int)Math.Round(r.x * ss), (int)Math.Round(r.y * ss), (int)Math.Round((r.x + r.w) * ss), (int)Math.Round((r.y + r.h) * ss));
        NSRect b = CGDisplayBounds(main);
        var bounds = Scaled(b);
        list.Add(new Screen(true, "DISPLAY1", bounds, Rectangle.FromLTRB(bounds.Left, bounds.Top + (int)Math.Round(25 * ss), bounds.Right, bounds.Bottom)));
        for (int i = 0; i < count; i++)
        {
            if (ids[i] == main) continue;
            b = CGDisplayBounds(ids[i]);
            bounds = Scaled(b);
            list.Add(new Screen(false, $"DISPLAY{list.Count + 1}", bounds, bounds));
        }
        return list.ToArray();
    }

    /// <summary>The main display's DPI as Windows would give it -- 96 per point, so 192 on a Retina
    /// display: its pixels over its points. From CoreGraphics, which any thread may ask; 0 if there
    /// is no display.</summary>
    internal static int MainDisplayDpi()
    {
        try
        {
            IntPtr mode = CGDisplayCopyDisplayMode(CGMainDisplayID());
            if (mode == IntPtr.Zero) return 0;
            nuint pixels = CGDisplayModeGetPixelWidth(mode), points = CGDisplayModeGetWidth(mode);
            CGDisplayModeRelease(mode);
            return points == 0 ? 0 : (int)Math.Round(96.0 * pixels / points);
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CoreGraphics)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CoreGraphics)] private static extern nuint CGDisplayModeGetWidth(IntPtr mode);
    [DllImport(CoreGraphics)] private static extern void CGDisplayModeRelease(IntPtr mode);
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
        double ss = ScreenScale;
        Rectangle Win32(NSRect r) => Rectangle.FromLTRB((int)Math.Round(r.x * ss), (int)Math.Round((primaryHeight - r.y - r.h) * ss),
                                                       (int)Math.Round((r.x + r.w) * ss), (int)Math.Round((primaryHeight - r.y) * ss));
        var list = new Screen[n];
        for (int i = 0; i < n; i++)
        {
            IntPtr screen = SendPtrNUInt(screens, Sel("objectAtIndex:"), (nuint)i);
            list[i] = new Screen(i == 0, $"DISPLAY{i + 1}", Win32(SendRectRet(screen, Sel("frame"))), Win32(SendRectRet(screen, Sel("visibleFrame"))));
        }
        bool same = list.Length == s_screens.Length && s_screensScale == ss;
        s_screensScale = ss;
        for (int i = 0; same && i < list.Length; i++)
            same = list[i].Bounds == s_screens[i].Bounds && list[i].WorkingArea == s_screens[i].WorkingArea;
        if (same) return;
        // Against what was said before -- the last reading, or the guess handed out before the first
        // -- at the same scale (a scale change is a DPI change, told otherwise).
        Screen[] before = s_screens.Length > 0 ? s_screens : s_guessed;
        if (before.Length == list.Length)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].Bounds != before[i].Bounds) s_displayOwed = true;
                else if (list[i].WorkingArea != before[i].WorkingArea) s_workAreaOwed = true;
            }
        }
        else if (before.Length > 0) s_displayOwed = true;
        if (s_displayOwed) s_workAreaOwed = true;
        s_guessed = Array.Empty<Screen>();
        s_screens = list;
        if (rescan) Screen.Rescan();
    }

    /// <summary>Form.WindowState was set: miniaturize, zoom or restore the window. Nothing listened
    /// here (only the Windows host did), so a form's own minimize and maximize buttons did nothing.</summary>
    private static void ApplyFrameAttributes(Form form)
    {
        CocoaHost host = null;
        lock (s_live)
            foreach (CocoaHost h in s_live)
                if (ReferenceEquals(h._form, form)) { host = h; break; }
        if (host == null) return;
        ScreenCaptureMode mode = form.FormScreenCaptureMode;
        OnMain(() =>
        {
            if (host._window != IntPtr.Zero && !host._closed) ApplySharingType(host._window, mode);
        });
    }

    // NSWindowSharingNone keeps the window out of screenshots, recordings and screen sharing (WDA_EXCLUDEFROMCAPTURE;
    // macOS has no black-box WDA_MONITOR, so HideContent hides it too); NSWindowSharingReadOnly is the default
    private static void ApplySharingType(IntPtr window, ScreenCaptureMode mode) =>
        SendVoidNInt(window, Sel("setSharingType:"), mode == ScreenCaptureMode.Allow ? 1 : 0);

    private static void ApplyWindowState(IntPtr formHandle, FormWindowState state)
    {
        CocoaHost host = null;
        lock (s_live)
            foreach (CocoaHost h in s_live)
                if (!h._form.IsDisposed && h._form.IsHandleCreated && h._form.Handle == formHandle) { host = h; break; }
        if (host == null) return;
        Interlocked.Increment(ref host._stateRequests);
        OnMain(() =>
        {
            try { Apply(); } finally { Interlocked.Decrement(ref host._stateRequests); }
        });

        void Apply()
        {
            IntPtr window = host._window;
            if (window == IntPtr.Zero || host._closed) return;
            if (s_traceFrames) Console.WriteLine($"frame: '{host._form.Text}' state {state} requested; iconic={(nint)Send(window, Sel("isMiniaturized")) != 0} zoomed={(nint)Send(window, Sel("isZoomed")) != 0}");
            host.ChangeState(state);
        }
    }

    // State changes asked of the window and not yet carried out (ApplyWindowState, queued to the main
    // thread): until they are, the driver holds the state asked for, not the window's.
    private int _stateRequests;

    // Where the window was before it was maximized: what Restore puts it back to when AppKit does not call
    // the window zoomed (a form that lies past the screen by its frame, CoversVisibleFrame).
    private NSRect _restoreFrame;

    /// <summary>Minimize, maximize or restore the window (main thread), then report what it is.</summary>
    private void ChangeState(FormWindowState state)
    {
        IntPtr window = _window;
        bool iconic = (nint)Send(window, Sel("isMiniaturized")) != 0, zoomed = (nint)Send(window, Sel("isZoomed")) != 0;
        bool maximized = zoomed || _reportedState == FormWindowState.Maximized && CoversVisibleFrame();
        Interlocked.Increment(ref _stateRequests);
        try
        {
            if (state == FormWindowState.Minimized) { if (!iconic) SendVoidPtr(window, Sel("miniaturize:"), IntPtr.Zero); }
            else
            {
                if (iconic) SendVoidPtr(window, Sel("deminiaturize:"), IntPtr.Zero);
                if (state == FormWindowState.Maximized && !maximized)
                {
                    _restoreFrame = SendRectRet(window, Sel("frame"));
                    SendVoidPtr(window, Sel("zoom:"), IntPtr.Zero);
                }
                else if (state == FormWindowState.Normal && maximized)
                {
                    if (zoomed) SendVoidPtr(window, Sel("zoom:"), IntPtr.Zero);
                    else if (_restoreFrame.w > 0 && _restoreFrame.h > 0)
                        SendVoidRectBoolBool(window, Sel("setFrame:display:animate:"), _restoreFrame, true, true);
                    else SendVoidPtr(window, Sel("zoom:"), IntPtr.Zero);      // AppKit's own idea of the window's size
                }
            }
        }
        finally { Interlocked.Decrement(ref _stateRequests); }
        SyncFrame();
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
            : (nint)Send(_window, Sel("isZoomed")) != 0 || _reportedState == FormWindowState.Maximized && CoversVisibleFrame()
                ? FormWindowState.Maximized : FormWindowState.Normal;
        // The driver's state is checked too, unless a change asked of the window is still on its way: a state
        // the window did not take (a maximize while the screens were changing) stayed the form's for good,
        // and its caption said Restore over a window that was not maximized, which Restore did nothing to.
        if (state == _reportedState && (Volatile.Read(ref _stateRequests) != 0 || _driver.GetWindowState(_form.Handle) == state)) return;
        _reportedState = state;
        _driver.HostWindowState(_form.Handle, state);
    }

    /// <summary>
    /// Whether the window covers the whole visible frame of its screen. A maximized Win32 window lies past
    /// its work area by its frame, and a form that draws its own frame puts itself there (DotNetBar's
    /// ribbon form: -5, -4 and 9 x 9 larger); AppKit, whose zoomed frame is exactly the visible one, calls
    /// that not zoomed -- reported so, the form restored itself the moment it was maximized.
    /// </summary>
    private bool CoversVisibleFrame()
    {
        IntPtr screen = Send(_window, Sel("screen"));
        if (screen == IntPtr.Zero) return false;
        NSRect v = SendRectRet(screen, Sel("visibleFrame")), f = SendRectRet(_window, Sel("frame"));
        return f.x <= v.x + 0.5 && f.y <= v.y + 0.5 && f.x + f.w >= v.x + v.w - 0.5 && f.y + f.h >= v.y + v.h - 0.5;
    }

    /// <summary>
    /// A maximized window the system moved or resized -- the screens changed under it: another primary
    /// display, another resolution, a display gone -- is maximized again on the screen it is on, as Windows
    /// keeps a maximized window maximized and fits it to its monitor's work area. AppKit put it back in the
    /// middle of the new screen at a size of its own, and the form, still maximized, could not be restored.
    /// Not a move the user makes (a drag of the window, a live resize) nor a change asked of it.
    /// </summary>
    private void RefitMaximized()
    {
        if (_reportedState != FormWindowState.Maximized || Volatile.Read(ref _stateRequests) != 0 || _liveResizing || _windowMoving) return;
        if ((nint)Send(_window, Sel("isMiniaturized")) != 0 || (nint)Send(_window, Sel("isZoomed")) != 0 || CoversVisibleFrame()) return;
        if (NativeBounds() == _knownBounds) return;
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' maximized, moved by the system to {NativeBounds()}: maximized again");
        Interlocked.Increment(ref _stateRequests);
        try { SendVoidPtr(_window, Sel("zoom:"), IntPtr.Zero); }
        finally { Interlocked.Decrement(ref _stateRequests); }
    }

    private void SyncFrame()
    {
        if (_window == IntPtr.Zero || _form.IsDisposed) return;
        RefitMaximized();
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
            // a step of the user's drag of the window (the size unchanged): the form is asked where it goes (WM_MOVING),
            // and where it says other than where the pointer took it, the window follows it on the next pass
            if (_windowMoving && native.Size == _knownBounds.Size && native.Location != _knownBounds.Location
                && _driver is XplatUIWebGpu md && md.HostMoving(_form.Handle, native) is var moved && moved != native)
            {
                _knownBounds = native;
                XplatUI.SetWindowPos(_form.Handle, moved.X, moved.Y, moved.Width, moved.Height);
                return;
            }
            _knownBounds = native;
            if (_form.Bounds != native) XplatUI.SetWindowPos(_form.Handle, native.X, native.Y, native.Width, native.Height);
            return;
        }

        Rectangle managed = _form.Bounds;
        if (managed != _knownBounds && managed.Width > 0 && managed.Height > 0)
        {
            // Moved or resized from code: the window follows.
            _knownBounds = managed;
            Rectangle inside = ClientScreen;
            NSRect content = CocoaRect(inside.Left, inside.Top, inside.Width, inside.Height);
            NSRect frame = SendRectRect(_window, Sel("frameRectForContentRect:"), content);
            SendVoidRectBool(_window, Sel("setFrame:display:"), frame, true);
        }
    }

    /// <summary>The form's bounds the window's content stands for (top-left origin, driver units): the
    /// content is the client area (Frame).</summary>
    private Rectangle NativeBounds()
    {
        NSRect frame = SendRectRet(_window, Sel("frame"));
        NSRect content = SendRectRect(_window, Sel("contentRectForFrameRect:"), frame);
        double screenHeight = PrimaryScreenHeight(), ss = ScreenScale;
        // the content is the client area; the form's bounds are it and the frame round it (Frame)
        return FormBoundsFor(new Rectangle((int)Math.Round(content.x * ss), (int)Math.Round((screenHeight - content.y - content.h) * ss),
                                           (int)Math.Round(content.w * _k), (int)Math.Round(content.h * _k)));
    }

    // Screen coordinates are anchored to the PRIMARY screen (the one with the menu bar), on both
    // sides: Win32's origin is its top-left, Cocoa's its bottom-left. (NSScreen.mainScreen is the
    // screen of the key window, which moves.)
    // (read again at most once a second: NSScreen.screens asks the window server, and the frame is
    // synchronised on every pump -- MacInterop keeps it)
    // ---- accessibility: the form as VoiceOver asks it (AutomationSource, CocoaAccessibility) ----------------
    //
    // The content view is of a class that answers NSAccessibility's container selectors (CocoaAccessibility adds
    // them, as to WPF's windows' views): its one child is the form's tree, which knows the window by the view.
    // The tree's bounds are top-left screen pixels at the main screen's backing scale (what CocoaAccessibility
    // turns into AppKit's points); a driver position is a point per ScreenScale units, a size per _k.

    private static IntPtr s_accessibleViewClass;

    private static IntPtr AccessibleViewClass()
    {
        if (s_accessibleViewClass != IntPtr.Zero) return s_accessibleViewClass;
        IntPtr cls = objc_getClass("WfAccessibleFormView");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Cls("NSView"), "WfAccessibleFormView", UIntPtr.Zero);
            WindowsBase::MS.Internal.Interop.CocoaAccessibility.AddViewAccessibility(cls);
            // a form is a drop target of the system's drag and drop (CocoaHost.DragDrop.cs)
            WindowsBase::MS.Internal.Interop.ForeignDragDrop.AddDestinationMethods(cls);
            objc_registerClassPair(cls);
        }
        return s_accessibleViewClass = cls;
    }

    // the primary display's (NSScreen.screens[0]), the scale CocoaAccessibility turns the pixels back into points with
    private static double BackingScale()
    {
        IntPtr screens = Send(Cls("NSScreen"), Sel("screens"));
        IntPtr primary = screens != IntPtr.Zero && (nint)Send(screens, Sel("count")) > 0
            ? SendPtrNUInt(screens, Sel("objectAtIndex:"), 0) : Send(Cls("NSScreen"), Sel("mainScreen"));
        double scale = primary == IntPtr.Zero ? 1 : SendDouble(primary, Sel("backingScaleFactor"));
        return scale > 0 ? scale : 1;
    }

    IntPtr WinFormsWebGpu.Accessibility.IA11yHostSite.Handle => _imageView;

    Form WinFormsWebGpu.Accessibility.IA11yHostSite.Form => _closed ? null : _form;

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryMapToScreen(Rectangle driverRect, out double x, out double y, out double width, out double height)
    {
        double ss = ScreenScale, k = _k > 0 ? _k : 1, bs = BackingScale();
        x = driverRect.X / ss * bs;
        y = driverRect.Y / ss * bs;
        width = driverRect.Width / k * bs;
        height = driverRect.Height / k * bs;
        return !_closed;
    }

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryMapFromScreen(double x, double y, out Point driverPoint)
    {
        double ss = ScreenScale, bs = BackingScale();
        driverPoint = new Point((int)Math.Floor(x / bs * ss), (int)Math.Floor(y / bs * ss));
        return !_closed;
    }

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryWindowRect(out double x, out double y, out double width, out double height) =>
        ((WinFormsWebGpu.Accessibility.IA11yHostSite)this).TryMapToScreen(_form.Bounds, out x, out y, out width, out height);

    private static double PrimaryScreenHeight()
    {
        double h = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.PrimaryScreenHeight();
        return h > 0 ? h : 900;
    }

    // a struct of more than 16 bytes comes back through objc_msgSend_stret on x86_64 (an Intel Mac); arm64 has
    // only objc_msgSend, which returns any struct
    private static NSRect SendRectRect(IntPtr r, IntPtr s, NSRect a) => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
        ? SendRectRectStret(r, s, a) : SendRectRectArm64(r, s, a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRectArm64(IntPtr r, IntPtr s, NSRect a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern NSRect SendRectRectStret(IntPtr r, IntPtr s, NSRect a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRect(IntPtr r, IntPtr s, NSRect a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRectBool(IntPtr r, IntPtr s, NSRect a, [MarshalAs(UnmanagedType.I1)] bool b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRectBoolBool(IntPtr r, IntPtr s, NSRect a, [MarshalAs(UnmanagedType.I1)] bool b, [MarshalAs(UnmanagedType.I1)] bool c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNUInt(IntPtr r, IntPtr s, nuint a);

    /// <summary>
    /// Where the form asked to be (its Location, which StartPosition has already resolved, and its
    /// size, in driver units), in Cocoa's coordinates: points, origin at the bottom-left of the main
    /// screen, measured to the window's bottom edge. Main thread only (NSScreen).
    /// </summary>
    private NSRect CocoaRect(int left, int top, double w, double h)
    {
        double screenHeight = PrimaryScreenHeight(), ss = ScreenScale;
        w /= _k; h /= _k;
        return new NSRect { x = left / ss, y = screenHeight - top / ss - h, w = w, h = h };
    }

    // ---- the frame Windows gives the form ----------------------------------------------------
    //
    // The driver models a top-level form's non-client frame as Windows 11's (XplatUIWebGpu.TopLevelFrame):
    // Form.Bounds is the window and ClientSize what is inside it, 16 x 39 smaller for a sizable form at
    // 96 dpi, as on Windows. The native window's content view is the CLIENT area; macOS draws its own
    // title bar above it. Before, the frame was none here and the form's bounds were the content: an app
    // that sizes a form by Size, MinimumSize or MaximumSize got a client 16 x 39 larger than on Windows.
    // A form that takes the non-client area over (WM_NCCALCSIZE: DotNetBar's ribbon form) has none.

    private Padding Frame => _form.IsHandleCreated && _driver is XplatUIWebGpu d ? d.FrameOf(_form.Handle) : Padding.Empty;

    /// <summary>The form's client area on the screen (driver units): what the native content shows.</summary>
    private Rectangle ClientScreen
    {
        get
        {
            Padding f = Frame;
            Rectangle b = _form.Bounds;
            return new Rectangle(b.X + f.Left, b.Y + f.Top, Math.Max(0, b.Width - f.Horizontal), Math.Max(0, b.Height - f.Vertical));
        }
    }

    /// <summary>A point in the native content (points, bottom-left origin) on the driver's screen: where the
    /// pointer is, by where the window is on the screen now -- not by the form's bounds, which the application
    /// may have changed since and the window not yet followed (SyncFrame, on the next pump). Measured from the
    /// form's height, a status bar's size grip setting the form's size in its WM_MOUSEMOVE saw the pointer
    /// move by as much as the form had shrunk: each move shrank it again, and the window jumped up and down.</summary>
    private System.Drawing.Point ContentToDriver(NSPoint loc)
    {
        NSRect c = ContentScreenRect();
        double ss = ScreenScale;
        int left = (int)Math.Round(c.x * ss), top = (int)Math.Round((PrimaryScreenHeight() - c.y - c.h) * ss);
        return new System.Drawing.Point(left + (int)(loc.x * _k), top + (int)((c.h - loc.y) * _k));
    }

    /// <summary>The form's bounds for a client area on the screen.</summary>
    private Rectangle FormBoundsFor(Rectangle client)
    {
        Padding f = Frame;
        return new Rectangle(client.X - f.Left, client.Y - f.Top, client.Width + f.Horizontal, client.Height + f.Vertical);
    }

    /// <summary>A point on the screen (Cocoa's) in the driver's screen space, by this window: its
    /// content's origin is the form's client origin there.</summary>
    private System.Drawing.Point ScreenToDriver(NSPoint p)
    {
        NSRect c = ContentScreenRect();
        Rectangle client = ClientScreen;
        return new System.Drawing.Point(client.Left + (int)Math.Floor((p.x - c.x) * _k), client.Top + (int)Math.Floor((c.y + c.h - p.y) * _k));
    }

    // a struct of more than 16 bytes comes back through objc_msgSend_stret on x86_64 (an Intel Mac); arm64 has
    // only objc_msgSend, which returns any struct
    private static NSRect SendRectRet(IntPtr r, IntPtr s) => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
        ? SendRectRetStret(r, s) : SendRectRetArm64(r, s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRetArm64(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern NSRect SendRectRetStret(IntPtr r, IntPtr s);

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


    // Overlay the blinking text caret onto the composite (the backing bitmaps don't contain it).
    private void DrawCaret(Bitmap dest)
    {
        if (!_driver.GetCaret(_form.Handle, out int x, out int y, out int w, out int h)) return;
        if ((BlinkMs() / 530) % 2 != 0) return;   // ~530ms blink
        w = Math.Max(1, w);
        using var g = Graphics.FromImage(dest);
        using var b = new SolidBrush(Color.Black);
        g.FillRectangle(b, x, y, w, h);
    }

    /// <summary>Inject a click at a WinForms screen point (as a real NSEvent click would route).</summary>
    public void InjectClickScreen(int x, int y) => _driver.InjectClick(x, y);

    /// <summary>Save the current composited frame (what's on screen) to a PNG for verification.</summary>
    public IntPtr NativeWindow => _window;

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
        WinFormsWebGpu.Accessibility.AutomationSource.Unregister(_imageView);
        _form.VisibleChanged -= OnFormVisibleChanged;
        if (_offMain)
        {
            _closed = true;
            OnMain(() =>
            {
                IntPtr w = _window;
                DisableDragDrop(_imageView);
                _window = IntPtr.Zero;
                _imageView = IntPtr.Zero;
                if (w != IntPtr.Zero) Send(w, Sel("close"));
            });
            return;
        }
        if (_window == IntPtr.Zero) return;
        ReleaseModalParent();
        StopObservingLiveResize();
        s_live.Remove(this);
        ClosePopupLayer();
        IntPtr win = _window;
        _window = IntPtr.Zero;
        Send(win, Sel("close"));
    }

    // WF_TEST_RESIZE=1 (diagnostics): 25 s after the window is up, its frame is grown and shrunk through
    // AppKit (setFrame:display:, which posts the resize notification a user's drag does), 40 steps,
    // and the cost of each is traced (WF_TRACE_RESIZE).
    private static readonly bool s_testResize = Environment.GetEnvironmentVariable("WF_TEST_RESIZE") == "1";
    private long _testResizeAt;
    private bool _testResized;

    private void TestResize()
    {
        if (!s_testResize || _testResized || _window == IntPtr.Zero) return;
        if (_testResizeAt == 0) { _testResizeAt = Environment.TickCount64 + 25000; return; }
        if (Environment.TickCount64 < _testResizeAt) return;
        _testResized = true;
        // in front, so its presents get drawables (one behind other windows draws nothing)
        Send(App(), Sel("activateIgnoringOtherApps:"), (IntPtr)1);
        SendVoidPtr(_window, Sel("orderFrontRegardless"), IntPtr.Zero);
        NSRect f0 = SendRectRet(_window, Sel("frame"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= 40; i++)
        {
            double d = (i <= 20 ? i : 40 - i) * 8;
            SendVoidRectBool(_window, Sel("setFrame:display:"), new NSRect { x = f0.x, y = f0.y - d, w = f0.w + d, h = f0.h + d }, true);
        }
        Console.Error.WriteLine($"[resize] test: 40 steps in {clock.ElapsedMilliseconds} ms");
    }

    public bool Pump()
    {
        // Events are the main thread's to read; see _offMain.
        if (_offMain) return !_closed && _form.Visible && !_form.IsDisposed;
        TestResize();
        // the simulated display change (WF_TEST_BACKING) comes with no window event to present on
        if (s_testBacking.Scale > 0 && !_testBackingSeen && Environment.TickCount64 >= s_testBacking.At) { _testBackingSeen = true; Present(); }

        HookWpfIfLoaded();
        if (Environment.TickCount64 - s_screensAt > 1000) RefreshScreens(rescan: true);
        RaiseScreenChanges();
        SyncFrame();
        EndSizeMoveIfOver();
        KeepModalAbove();
        ReportActivation();

        // Inside a resize callback (an app's Resize handler calling DoEvents) the queue is AppKit's
        // tracking loop's: see CocoaWindow.PumpEventsCore.
        while (s_inResize == 0 && !InAppKitTrackingMode())
        {
            IntPtr evt = SendNextEvent(App(), Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                unchecked((nuint)ulong.MaxValue), IntPtr.Zero, NSStr("kCFRunLoopDefaultMode"), true);
            if (evt == IntPtr.Zero) break;
            Route(evt);
            // What an event's handler did to the form's bounds is the window's before the next event, as a
            // SetWindowPos on Windows is: a size grip sizing the form at each WM_MOUSEMOVE had the window
            // follow only once the drag's moves stopped coming.
            SyncFrame();
        }
        // A miniaturized window is not visible to AppKit, but it is no less the form's: the form is
        // minimized, as it is on Windows while its window sits in the taskbar. Treated as closed, the
        // host went away the moment the window reached the Dock, and the still-visible form was given a
        // new window at once -- out of the Dock, with a native title bar over the form's own caption.
        return (nint)Send(_window, Sel("isVisible")) != 0 || (nint)Send(_window, Sel("isMiniaturized")) != 0;
    }

    /// <summary>
    /// Whether the run loop is turning in a mode of AppKit's own (NSEventTrackingRunLoopMode: a live
    /// resize, a window drag, a menu): an AppKit loop is on the stack, waiting on the queue for the
    /// events that end it. Its timers and sources still run -- WPF's dispatcher among them -- and a pump
    /// reached from one that read the queue took the button's release the resize loop was waiting for:
    /// the loop waited on, and the application took no input at all until it was killed. The resize
    /// callback's own guard (s_inResize) covers only what runs inside that callback. At the top level
    /// the run loop runs in no mode, and inside the dispatcher's own turn of it in the default one.
    /// </summary>
    private static bool InAppKitTrackingMode()
    {
        IntPtr current = CFRunLoopCopyCurrentMode(CFRunLoopGetCurrent());
        if (current == IntPtr.Zero) return false;
        try { return !SendBoolPtr(current, Sel("isEqualToString:"), NSStr("kCFRunLoopDefaultMode")); }
        finally { CFRelease(current); }
    }

    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFRunLoopCopyCurrentMode(IntPtr runLoop);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);

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
    private static Action<IntPtr, IntPtr> s_wpfKeyTo;
    private static volatile bool s_wpfLoaded;

    // In methods of their own, so WindowsBase is resolved only once it has loaded.
    private static void EnterWpfAppKitCallback() => WindowsBase::MS.Internal.Interop.CocoaWindow.EnterAppKitCallback();
    private static void ExitWpfAppKitCallback() => WindowsBase::MS.Internal.Interop.CocoaWindow.ExitAppKitCallback();

    private static void OnWpfLoaded()
    {
        if (s_wpfLoaded) return;
        s_wpfLoaded = true;
        RegisterDisabledPress();
    }

    // In a method of its own: the JIT resolves WindowsBase only when it is called, after it has loaded.
    private static void RegisterDisabledPress()
        => WindowsBase::MS.Internal.Interop.PlatformWindow.DisabledWindowPressed ??= RaiseModalOnDisabledPress;

    static CocoaHost()
    {
        // Form.WindowState set by the application (DotNetBar's own caption buttons post SC_MINIMIZE,
        // which the driver makes Minimized): the real window follows, as ShowWindow does on Windows.
        System.Windows.Forms.XplatUIWebGpu.WindowStateRequested ??= ApplyWindowState;
        // Form.FormScreenCaptureMode: the window's sharing type (the frame's colours and corners are AppKit's own,
        // as they are a window manager's that does not take DWM's attributes)
        System.Windows.Forms.XplatUIWebGpu.FrameAttributesRequested ??= ApplyFrameAttributes;
        // for long work the main thread does outside WinForms (rewriting an assembly as it loads): it can
        // keep the work other threads queue for it moving (see MainWork)
        if (AppContext.GetData("WinFormsWebGpu.DrainMainWork") == null) AppContext.SetData("WinFormsWebGpu.DrainMainWork", (Action)DrainMainWork);
        // WPF's pump hands this host the events of its windows (CocoaWindow.ForeignEventHandler)
        AppContext.SetData("WinFormsWebGpu.CocoaEvents", (Func<IntPtr, bool>)RouteFromWpf);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { if (e.LoadedAssembly.GetName().Name == "WindowsBase") OnWpfLoaded(); };
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == "WindowsBase") OnWpfLoaded();
    }

    // Once WPF is in the process, give it its own events from our pump: its window class publishes
    // how it takes them (CocoaWindow.DispatchEvent) when it is first used, which is before WPF has a
    // window for an event to be for. It takes ours from its pump through the router published above.
    private static void HookWpfIfLoaded()
    {
        if (!s_wpfLoaded || s_wpfDispatch != null) return;
        s_wpfDispatch = AppContext.GetData("WpfWebGpu.CocoaWindow.DispatchEvent") as Action<IntPtr>;
    }

    // A dialog shown with ShowDialog() and no owner takes the active window for its owner on Windows, and
    // one centred on its parent (CenterParent) is centred on that window -- whichever framework made it. A
    // WPF application's About box is a WinForms form shown from a WPF window: the form has no Form to own
    // it (Form.CenterToParent then centres it on the screen), so its window is centred here on the window
    // the user was working in, the key window, when that is not one of ours. Kept on the key window's
    // screen, below the menu bar, as Windows keeps it in the owner's monitor's working area.
    private void CenterOnForeignOwner(ref NSRect rect)
    {
        if (_form.StartPosition != FormStartPosition.CenterParent || _form.Owner != null || !_form.Modal) return;
        IntPtr owner = Send(App(), Sel("keyWindow"));
        if (owner == IntPtr.Zero) owner = Send(App(), Sel("mainWindow"));
        if (owner == IntPtr.Zero || HostFor(owner) != null || (nint)Send(owner, Sel("isVisible")) == 0) return;
        NSRect o = SendRectRet(owner, Sel("frame"));
        double x = o.x + (o.w - rect.w) / 2, y = o.y + (o.h - rect.h) / 2;
        IntPtr screen = Send(owner, Sel("screen"));
        if (screen != IntPtr.Zero)
        {
            NSRect work = SendRectRet(screen, Sel("visibleFrame"));
            x = Math.Max(work.x, Math.Min(x, work.x + work.w - rect.w));
            y = Math.Max(work.y, Math.Min(y, work.y + work.h - rect.h));
        }
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' centred on the key window ({o.x},{o.y} {o.w}x{o.h})");
        rect.x = x; rect.y = y;
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

    /// <summary>
    /// The host a key AppKit gave to no window belongs to: the active form's (or the modal dialog over it).
    /// On Windows a foreground application always has an active window, and the keyboard is its focus
    /// window's. Here a closing window can leave AppKit with no key window at all -- a WPF dialog over a
    /// form (a configuration dialog, say) promotes only another WPF window when it goes -- and every key
    /// after it went to AppKit, which beeped: Delete and the arrows did nothing on the display until the
    /// form was clicked. The form's window is made key again, so the keys that follow are its own.
    /// </summary>
    private static CocoaHost HostForWindowlessKey(IntPtr evt)
    {
        if (Send(evt, Sel("window")) != IntPtr.Zero) return null;
        nint type = (nint)Send(evt, Sel("type"));
        if (type != 10 && type != 11 && type != 12) return null;
        if (Send(App(), Sel("keyWindow")) != IntPtr.Zero) return null;
        CocoaHost host = ModalHost();
        if (host == null && s_lastActive != null && s_live.Contains(s_lastActive) && s_lastActive._window != IntPtr.Zero && s_lastActive._form.Visible)
            host = s_lastActive;
        if (host == null)
            foreach (CocoaHost h in s_live)
                if (h._window != IntPtr.Zero && h._form.Visible && h._form.Enabled) { host = h; break; }
        if (host == null) return null;
        if (KeyTrace.On) KeyTrace.Log($"cocoa key event with no window -> the active form '{host._form.Text}', made key");
        SendVoidPtr(host._window, Sel("makeKeyWindow"), IntPtr.Zero);
        return host;
    }

    private static bool RouteFromWpf(IntPtr evt)
    {
        if (KeyTrace.On && (nint)Send(evt, Sel("type")) is 10 or 11)
            KeyTrace.Log($"cocoa key event (from WPF's pump) code {(nint)Send(evt, Sel("keyCode"))} window 0x{Send(evt, Sel("window")):x} host {(HostFor(Send(evt, Sel("window"))) != null)}");
        if (BlockedByModal(evt)) return true;
        CocoaHost host = HostFor(Send(evt, Sel("window"))) ?? HostUnderPointer(evt) ?? HostForWindowlessKey(evt);
        if (host == null) return FilterEmbeddedPress(evt);
        host.HandleEvent(evt);
        return true;
    }

    // ---- the loop's wait ------------------------------------------------------------------
    //
    // The message loop sleeps as a Cocoa application does, in AppKit's event wait, until there is
    // something to do: an event (input, a window's own), the next WinForms timer, a message posted from
    // another thread, or a new frame of hosted WPF content -- not a few milliseconds at a time, polling
    // (125 times a second, an idle app took 8% of a core). The run loop turns the whole time, so the WPF
    // dispatcher of the thread (an ElementHost's content) runs while the loop waits.

    // CocoaWindow's wake: an application-defined event of its subtype, which either pump drops
    private const nuint NSApplicationDefined = 15;
    private const short WakeSubtype = 0x5750;
    private static int s_wakePending;

    /// <summary>Whether a form is on screen through this host: its loop then waits in AppKit.</summary>
    internal static bool Active { get { lock (s_live) return s_live.Count > 0; } }

    /// <summary>Ends the main thread's <see cref="WaitForEvent"/>: from any thread (posting an event is
    /// AppKit's one thread-safe way into the main thread's wait).</summary>
    internal static void WakeLoop()
    {
        if (!Active || Interlocked.Exchange(ref s_wakePending, 1) == 1) return;
        IntPtr pool = objc_autoreleasePoolPush();
        try
        {
            IntPtr evt = SendOtherEvent(Cls("NSEvent"),
                Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                NSApplicationDefined, default, 0, 0, 0, IntPtr.Zero, WakeSubtype, 0, 0);
            if (evt == IntPtr.Zero) { Volatile.Write(ref s_wakePending, 0); return; }
            SendVoidPtrBool(App(), Sel("postEvent:atStart:"), evt, false);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Waits on the main thread until an event is queued (left for the pump to take), up to
    /// <paramref name="milliseconds"/>, the run loop running meanwhile.</summary>
    internal static void WaitForEvent(int milliseconds)
    {
        if (pthread_main_np() == 0) { Thread.Sleep(milliseconds); return; }
        IntPtr until = SendPtrDouble(Cls("NSDate"), Sel("dateWithTimeIntervalSinceNow:"), milliseconds / 1000.0);
        SendNextEvent(App(), Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
            unchecked((nuint)ulong.MaxValue), until, NSStr("kCFRunLoopDefaultMode"), false);
        // whoever takes the wake off the queue, this wait is over: the next wake is a new one
        Volatile.Write(ref s_wakePending, 0);
    }

    private static bool IsWake(IntPtr evt) =>
        (nuint)(nint)Send(evt, Sel("type")) == NSApplicationDefined && (short)(nint)Send(evt, Sel("subtype")) == WakeSubtype;

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOtherEvent(IntPtr receiver, IntPtr selector, nuint type, NSPoint location, nuint flags,
                                                double timestamp, nint windowNumber, IntPtr context, short subtype, nint data1, nint data2);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidPtrBool(IntPtr receiver, IntPtr selector, IntPtr a, [MarshalAs(UnmanagedType.I1)] bool b);
    [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr r, IntPtr s, double d);

    private static void Route(IntPtr evt)
    {
        if (IsWake(evt)) { Volatile.Write(ref s_wakePending, 0); return; }   // its work was the waking
        if (KeyTrace.On && (nint)Send(evt, Sel("type")) is 10 or 11)
        {
            IntPtr w = Send(evt, Sel("window"));
            KeyTrace.Log($"cocoa key event type {(nint)Send(evt, Sel("type"))} code {(nint)Send(evt, Sel("keyCode"))} window 0x{w:x} host {(HostFor(w) != null)} key 0x{Send(App(), Sel("keyWindow")):x} wpfDispatch {(s_wpfDispatch != null)} blocked {BlockedByModal(evt)}");
        }
        // A system drag a form started: AppKit tracks it from the events, wherever the pointer is.
        if (s_systemDragActive) { Send(App(), Sel("sendEvent:"), evt); return; }
        // the left button let go, wherever: no form is still sizing after it
        if ((nint)Send(evt, Sel("type")) == 2)
            lock (s_live)
                foreach (CocoaHost h in s_live) h._sizingHit = 0;
        if (BlockedByModal(evt)) return;
        CocoaHost host = HostFor(Send(evt, Sel("window"))) ?? HostUnderPointer(evt) ?? HostForWindowlessKey(evt);
        if (host != null) host.HandleEvent(evt);
        else if (FilterEmbeddedPress(evt)) return;
        else if (s_wpfDispatch != null) s_wpfDispatch(evt);
        else Send(App(), Sel("sendEvent:"), evt);
    }

    // ---- disabled windows and modal dialogs ---------------------------------------------
    //
    // ShowDialog disables every other form of the thread (EnableWindow FALSE), and Windows then gives a
    // disabled window no input at all: no mouse, no keys, not even a move of it by its caption -- and
    // neither to its child windows, an ElementHost's WPF tree among them. A press on one activates the
    // window that disabled it (its last active popup, the modal dialog) and beeps. Here AppKit took the
    // press, made the disabled form key and raised it over its dialog, and the canvas under it went on
    // taking clicks: app's Select Color dialog was neither in front nor in the way.

    /// <summary>Whether <paramref name="evt"/> is input for a disabled form's window (or one of its child
    /// windows): then it is dropped, and a press brings the modal dialog forward.</summary>
    private static bool BlockedByModal(IntPtr evt)
    {
        try
        {
            nint type = (nint)Send(evt, Sel("type"));
            bool press = type is 1 or 3 or 25;
            bool input = press || type is 2 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 22 or 26 or 27;
            if (!input) return false;
            IntPtr window = Send(evt, Sel("window"));
            CocoaHost host = HostFor(window);
            if (host == null && window != IntPtr.Zero) host = HostFor(Send(window, Sel("parentWindow")));
            if (host == null || host._form.IsDisposed || host._form.Enabled) return false;
            if (press && s_traceFrames)
                Console.WriteLine($"frame: press for disabled '{host._form.Text}' dropped: window {(window == host._window ? "its own" : window == host._popupWindow ? "its popup layer" : "a child")} (popups shown={host._popupShown})");
            if (press && ModalHost() is { } modal && modal._window != IntPtr.Zero)
            {
                // a press that fell on the dialog through a window wrongly above it is the dialog's
                // (StayAboveSiblings): raised now, the user's next press reaches it; no beep for it
                NSPoint at = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
                NSRect df = SendRectRet(modal._window, Sel("frame"));
                bool onDialog = at.x >= df.x && at.x < df.x + df.w && at.y >= df.y && at.y < df.y + df.h;
                if (!onDialog) NSBeep();
                SendVoidPtr(modal._window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // While a dialog is modal it is a child window of the form it blocks -- its owner, or the form that was
    // active when it came up (ShowDialog with no owner takes that one on Windows): AppKit keeps a child
    // above its parent whatever raises the parent, as Windows keeps a dialog above the window it
    // disabled. A child window also moves with its parent, which an owned window does not, but a
    // disabled window cannot be moved. Undone when the dialog goes (ReleaseModalParent).
    private IntPtr _modalParent;
    private static CocoaHost s_lastActive;

    private void KeepModalAbove()
    {
        if (_window == IntPtr.Zero) return;
        if (!_form.Modal || !_form.Visible) { ReleaseModalParent(); return; }
        if (_modalParent != IntPtr.Zero) { StayAboveSiblings(); return; }
        CocoaHost parent = null;
        foreach (CocoaHost h in s_live) if (h != this && h._form == _form.Owner) parent = h;
        if (parent == null && s_lastActive != this && s_lastActive != null && s_live.Contains(s_lastActive) && !s_lastActive._form.Enabled) parent = s_lastActive;
        IntPtr pw = parent?._window ?? IntPtr.Zero;
        // ...or, with no form to be owned by, the WPF window it disabled: Workbench's About box over its WPF
        // main window, which a click raised over the dialog -- the dialog went behind it, and the app sat
        // blocked by a dialog nobody could see.
        if (pw == IntPtr.Zero) pw = DisabledWpfWindow();
        if (pw == IntPtr.Zero) return;
        SendPtrNInt(pw, Sel("addChildWindow:ordered:"), _window, 1);   // NSWindowAbove
        _modalParent = pw;
        if (s_traceFrames) Console.WriteLine($"frame: '{_form.Text}' modal over '{parent?._form.Text ?? "a WPF window"}'");
    }

    /// <summary>The frontmost visible WPF window a modal loop has disabled (the platform layer's EnableWindow
    /// flag), as NSWindow; zero when WPF is not in the process or has none.</summary>
    private static IntPtr DisabledWpfWindow()
    {
        if (!s_wpfLoaded) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        nint bestIndex = nint.MaxValue;
        foreach (IntPtr h in WindowsBase::MS.Internal.Interop.PlatformWindow.DisabledWindows())
        {
            if (WindowsBase::MS.Internal.Interop.PlatformWindow.FromHandle(h) is not WindowsBase::MS.Internal.Interop.CocoaWindow cw || cw.IsBorderless) continue;
            IntPtr w = cw.Window;
            if (w == IntPtr.Zero || (nint)Send(w, Sel("isVisible")) == 0) continue;
            nint index = (nint)Send(w, Sel("orderedIndex"));   // 0 is frontmost
            if (index < bestIndex) { bestIndex = index; best = w; }
        }
        return best;
    }

    /// <summary>A press on a disabled WPF window while a dialog of this host is modal: Windows activates the
    /// dialog. (A WPF dialog puts its own answer in place while it is up.)</summary>
    private static void RaiseModalOnDisabledPress(IntPtr handle)
    {
        if (ModalHost() is { } modal && modal._window != IntPtr.Zero)
            SendVoidPtr(modal._window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
    }

    // Above the parent's other child windows too: an ElementHost's WPF windows are its children as
    // well, and one reordered as the form moves it or gives it the keyboard rose over the dialog, whose
    // caption then took no presses -- they went to the disabled form's canvas, and beeped.
    private void StayAboveSiblings()
    {
        IntPtr children = Send(_modalParent, Sel("childWindows"));
        nint n = children == IntPtr.Zero ? 0 : (nint)Send(children, Sel("count"));
        nint mine = (nint)Send(_window, Sel("orderedIndex"));
        for (nint i = 0; i < n; i++)
        {
            IntPtr c = SendPtrNUInt(children, Sel("objectAtIndex:"), (nuint)i);
            if (c == _window || (nint)Send(c, Sel("isVisible")) == 0) continue;
            if ((nint)Send(c, Sel("orderedIndex")) < mine)   // in front of the dialog
            {
                SendVoidPtr(_window, Sel("orderFront:"), IntPtr.Zero);
                return;
            }
        }
    }

    private void ReleaseModalParent()
    {
        if (_modalParent == IntPtr.Zero) return;
        if (_window != IntPtr.Zero) SendVoidPtr(_modalParent, Sel("removeChildWindow:"), _window);
        _modalParent = IntPtr.Zero;
    }

    /// <summary>The host of the innermost modal dialog on screen, if one is.</summary>
    private static CocoaHost ModalHost()
    {
        for (int i = Form.modal_dialogs.Count - 1; i >= 0; i--)
        {
            if (Form.modal_dialogs[i] is not Form f || f.IsDisposed || !f.Visible) continue;
            foreach (CocoaHost h in s_live) if (h._form == f && h._window != IntPtr.Zero) return h;
        }
        return null;
    }

    /// <summary>The forms this one owns -- Owner set, or a dialog shown over it -- kept above it, as Windows
    /// keeps an owned window above its owner.</summary>
    private void RaiseOwned()
    {
        nint below = (nint)Send(_window, Sel("windowNumber"));
        foreach (CocoaHost h in s_live)
        {
            if (h == this || h._window == IntPtr.Zero || h._form.IsDisposed || !h._form.Visible || h._form.Owner != _form) continue;
            SendNIntNInt(h._window, Sel("orderWindow:relativeTo:"), 1, below);   // NSWindowAbove
            h.RaiseOwned();
        }
    }

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")] private static extern void NSBeep();
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNIntNInt(IntPtr r, IntPtr s, nint a, nint b);

    /// <summary>Where the pointer is, in Win32's screen coordinates (top-left of the primary
    /// screen), wherever it is: over the form, an embedded window, or none of the app's.</summary>
    /// <summary>Whether the left mouse button is down now, as the window server has it (and not as the
    /// last event this process saw says): null when it cannot be asked.</summary>
    internal static bool? RealLeftButtonDown()
    {
        try { return ((nuint)Send(Cls("NSEvent"), Sel("pressedMouseButtons")) & 1) != 0; }
        catch { return null; }
    }

    internal static bool TryRealCursor(out int x, out int y)
    {
        x = y = 0;
        try
        {
            NSPoint p = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
            double ss = ScreenScale;
            x = (int)Math.Floor(p.x * ss);
            y = (int)Math.Floor((PrimaryScreenHeight() - p.y) * ss);
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
            var screen = host.ScreenToDriver(new NSPoint { x = frame.x + loc.x, y = frame.y + loc.y });
            host._driver.NotePointer(screen.X, screen.Y);
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
    private double _wheelUnits, _hwheelUnits;

    private void HandleScrollWheel(IntPtr evt)
    {
        double dy = SendDouble(evt, Sel("scrollingDeltaY"));
        bool precise = Send(evt, Sel("hasPreciseScrollingDeltas")) != IntPtr.Zero;
        // Sideways too: WM_MOUSEHWHEEL, positive to the right -- AppKit's delta is positive the other way.
        double dx = -SendDouble(evt, Sel("scrollingDeltaX"));
        _hwheelUnits += precise ? dx * 120 / PointsPerNotch : dx * 120;
        int across = (int)(_hwheelUnits / 120);
        if (across != 0)
        {
            _hwheelUnits -= across * 120;
            System.Drawing.Point hp = ContentToDriver(LocationInContent(evt, out _));
            _driver.InjectHWheel(hp.X, hp.Y, across * 120);
        }
        // Both are what the user's scroll direction setting makes of the gesture, as Windows'
        // delta is: positive scrolls the content up into view, as the wheel turned away does.
        _wheelUnits += precise ? dy * 120 / PointsPerNotch : dy * 120;
        int notches = (int)(_wheelUnits / 120);
        if (notches == 0)
        {
            if (across != 0) { Application.DoEvents(); Present(); }
            return;
        }
        _wheelUnits -= notches * 120;

        NSPoint loc = LocationInContent(evt, out _);
        System.Drawing.Point wp = ContentToDriver(loc);
        _driver.InjectWheel(wp.X, wp.Y, notches * 120);
        Application.DoEvents();
        Present();
    }


    // ---- sizing from the form's own border ------------------------------------------------
    //
    // Windows' SC_SIZE loop: the window's frame follows the pointer from the press on the border to
    // the release, within the form's minimum and maximum size. Each change of frame reaches the form
    // through the resize notification (OnWindowResized), as a live resize does.

    private int _sizingHit;

    /// <summary>The left button is down as the forms have been told (pressed in one, not yet released to them).</summary>
    private static bool s_formLeftDown;
    private NSRect _sizingFrame;
    private NSPoint _sizingStart;

    private bool IsResizable() => _form.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow
                                  && _form.WindowState == FormWindowState.Normal;

    private void BeginSizing(int hit, NSPoint loc)
    {
        _sizingHit = hit;
        SizeMove(true);
        _sizingFrame = SendRectRet(_window, Sel("frame"));
        _sizingStart = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
    }

    /// <summary>A mouse event during sizing: the frame follows a drag, a release ends it. True when
    /// the event was the sizing's.</summary>
    private bool ContinueSizing(nint type, NSPoint loc)
    {
        // A sizing ends with the button, wherever it is let go: a release that went elsewhere (outside the
        // window, to AppKit) left it running, and the next drag anywhere in the form was measured from where
        // the old one began -- the window jumped to 541 x 39 in one step. A new press is not part of it either.
        if (type == 2 || RealLeftButtonDown() == false) { _sizingHit = 0; return type == 2; }
        if (type == 1) { _sizingHit = 0; return false; }
        if (type != 6) return type == 5;
        // The pointer on the screen, not the event's point in the window: that is measured from where
        // the window was when the event was queued, and each step of the drag moves the window. Taken
        // against the frame as it is now, a queued event counted the last step again -- the bottom
        // edge ran away from the pointer, faster each step, until the window was 0 high and gone.
        NSPoint p = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
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
        w = Math.Max(w, (min.Width > 0 ? min.Width : 136) / _k);
        h = Math.Max(h, (min.Height > 0 ? min.Height : 39) / _k);
        if (max.Width > 0) w = Math.Min(w, max.Width / _k);
        if (max.Height > 0) h = Math.Min(h, max.Height / _k);
        // the window has its say, as Windows asks it at each step (WM_SIZING): a size it keeps in proportion, or in steps
        Rectangle form = _form.Bounds;
        int fw = (int)Math.Round(w * _k), fh = (int)Math.Round(h * _k);
        var proposed = new Rectangle(left ? form.Right - fw : form.Left, top ? form.Bottom - fh : form.Top, fw, fh);
        int edge = (left ? 1 : right ? 2 : 0) + (top ? 3 : bottom ? 6 : 0);   // WMSZ_LEFT .. WMSZ_BOTTOMRIGHT
        Rectangle asked = _driver.HostSizing(_form.Handle, edge, proposed);
        if (asked.Width > 0 && asked.Height > 0) { w = asked.Width / _k; h = asked.Height / _k; }
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

    /// <summary>SC_SIZE, or WM_NCLBUTTONDOWN on a sizing edge, from inside the press (a StatusStrip's size
    /// grip, a form that draws its own frame): this host's sizing loop, from that edge. Nothing on macOS
    /// took it, so the grip did nothing. As Windows' size loop does, it takes the mouse: the control that
    /// had it loses it, and the release is the loop's.</summary>
    private static void SizeWindowWithPointer(IntPtr handle, int edge)
    {
        CocoaHost host = t_eventHost;
        if (host == null || t_event == IntPtr.Zero || host._window == IntPtr.Zero) return;
        if (!host._form.IsHandleCreated || host._form.Handle != handle || !host.IsResizable()) return;
        if (edge < 1 || edge > 8) return;
        host._formHasMouse = false;
        s_formLeftDown = false;
        if (host._driver.GrabHandle != IntPtr.Zero) host._driver.UngrabWindow(host._driver.GrabHandle);
        host.BeginSizing(edge + 9, default);   // WMSZ_LEFT .. WMSZ_BOTTOMRIGHT is HTLEFT .. HTBOTTOMRIGHT less 9
    }

    private void HandleEvent(IntPtr evt)
    {
        XplatUIWebGpu.BeginWindowMove ??= MoveWindowWithPointer;
        XplatUIWebGpu.BeginWindowSize ??= SizeWindowWithPointer;
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
        if (_form.IsDisposed || _form.Disposing)
            return;
        nint type = (nint)Send(evt, Sel("type"));
        // NSEventType: LeftMouseDown=1, LeftMouseUp=2, RightMouseDown=3, RightMouseUp=4,
        // MouseMoved=5, LeftMouseDragged=6, RightMouseDragged=7, OtherMouseDown=25, OtherMouseUp=26,
        // OtherMouseDragged=27 (the middle button and the extra ones, by buttonNumber).
        if ((type >= 1 && type <= 7) || (type >= 25 && type <= 27))
        {
            NSPoint loc = LocationInContent(evt, out bool viaLayer);
            if (_sizingHit != 0 && ContinueSizing(type, loc)) return;
            // the point in the content in driver units (still bottom-up); loc stays in points for AppKit
            NSPoint at = new NSPoint { x = loc.x * _k, y = loc.y * _k };
            // A press on a sizing border the form draws itself sizes the window, as Windows' default
            // handling of HTLEFT..HTBOTTOMRIGHT does. AppKit sizes only from its own frame: its title
            // bar zone at the top, nothing along the other edges, which the content covers.
            if (type == 1 && _ownCaption && !_formHasMouse && !viaLayer && IsResizable())
            {
                System.Drawing.Point sp = ContentToDriver(loc);
                int sizing = _driver.FrameHitTest(_form, sp.X, sp.Y);
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
            NSRect nativeContent = ContentScreenRect();
            bool inContent = at.y >= 0 && at.y < nativeContent.h * _k && at.x >= 0 && at.x < nativeContent.w * _k;
            bool atEdge = loc.x < EdgeBand || loc.x >= nativeContent.w - EdgeBand || loc.y < EdgeBand;
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
                System.Drawing.Point hp = ContentToDriver(loc);
                // The deepest window there answers first, as Windows asks it (XplatUIWebGpu.FrameHitTest).
                // Asked of the form alone, a maximized ribbon form called its caption buttons caption: a
                // press on Minimize or Restore dragged the window and did nothing else.
                int hit = _driver.FrameHitTest(_form, hp.X, hp.Y);
                if (hit == HTCAPTION)
                {
                    // a double click is the form's WM_NCLBUTTONDBLCLK, which maximizes or restores it as its state
                    // says (DefWndProc); zoom: here toggled AppKit's idea, which a form lying past the screen by
                    // its frame is not, and maximized it again instead of restoring it
                    if ((nint)Send(evt, Sel("clickCount")) >= 2)
                        XplatUI.SendMessage(_form.Handle, (Msg)0x00A3 /* WM_NCLBUTTONDBLCLK */, (IntPtr)HTCAPTION, (IntPtr)((hp.Y << 16) | (hp.X & 0xffff)));
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
            if (type == 1 || type == 3 || type == 25) _formHasMouse = true;
            else if (type == 2 || type == 4 || type == 26) _formHasMouse = false;
            // Window point (Cocoa: bottom-left origin) to the driver's screen space, where it
            // hit-tests: the form's client area is drawn at the form's location there. Window
            // coordinates alone were right only while every form sat at (0,0) -- which stopped
            // being true when the form began to report where the window really is.
            System.Drawing.Point dp = ContentToDriver(loc);
            int cx = dp.X, cy = dp.Y;
            if (s_traceInput) Console.WriteLine($"[cocoa input] type={type} window=({loc.x},{loc.y}) -> driver=({cx},{cy}) form={_form.Bounds}");
            // A bare move is Windows' WM_SETCURSOR, which ends a Cursor.Current; a drag (captured) keeps it.
            if (type == 5) _driver.EndCursorOverride();
            // A release the form never had (AppKit took it ending a window drag, or it fell outside every window
            // of the application) is given to it before anything else, as Windows always delivers it to the window
            // that has the mouse: a control that began a drag of its own on the press went on acting on every later
            // move -- a docking bar sized the main form after the pointer, down to nothing.
            if (s_formLeftDown && (type == 5 || type == 6) && RealLeftButtonDown() == false)
            {
                s_formLeftDown = false;
                _driver.InjectMouseUp(cx, cy);
                if (type == 6) type = 5;
            }
            if (type == 1) s_formLeftDown = true;
            else if (type == 2) s_formLeftDown = false;
            switch (type)
            {
                case 1: _driver.InjectMouseDown(cx, cy); break;
                case 2: _driver.InjectMouseUp(cx, cy); break;
                case 3: _driver.InjectRightDown(cx, cy); break;
                case 4: _driver.InjectRightUp(cx, cy); break;
                case 5: _driver.InjectMouseMove(cx, cy, false); break;
                case 6: _driver.InjectMouseMove(cx, cy, true); break;
                // a drag with another button: the move carries whatever is down (the driver knows)
                case 7: case 27: _driver.InjectMouseMove(cx, cy, false); break;
                case 25: case 26:
                {
                    // buttonNumber 2 is the middle button, 3 and 4 the extra ones (back, forward): XBUTTON1, XBUTTON2
                    nint button = (nint)Send(evt, Sel("buttonNumber"));
                    bool down = type == 25;
                    if (button == 2) { if (down) _driver.InjectMiddleDown(cx, cy); else _driver.InjectMiddleUp(cx, cy); }
                    else if (button == 3 || button == 4)
                    {
                        if (down) _driver.InjectXButtonDown(cx, cy, (int)button - 2);
                        else _driver.InjectXButtonUp(cx, cy, (int)button - 2);
                    }
                    break;
                }
            }
            Application.DoEvents();   // let the control repaint (pressed/hover) into its backbuffer
            Present();                // show it immediately
            if (type == 5) Send(App(), Sel("sendEvent:"), evt);
            if (type is 5 or 6 or 7 or 27) ApplyCursor();
            return;
        }
        else if (type == 22) // NSEventTypeScrollWheel -> WM_MOUSEWHEEL at the pointer
        {
            HandleScrollWheel(evt);
            return;
        }
        else if (type == 10 || type == 11 || type == 12) // KeyDown, KeyUp, FlagsChanged
        {
            HandleKey(evt, type);
            return;   // the form's keys are its own; AppKit gets none of them
        }
        Send(App(), Sel("sendEvent:"), evt);
    }

    // ---- the close button ------------------------------------------------------------------
    //
    // Windows' close button is a WM_SYSCOMMAND/SC_CLOSE to the form, which decides: it closes (its own
    // Close, which takes the window down through this host) or stays (FormClosing cancelled). AppKit's
    // closed the window itself, and the host, finding the window gone and the form still shown, gave
    // the form a new one: the login dialog animated away and came straight back. A delegate asks the
    // form instead and keeps the window (windowShouldClose: NO); the host's own close (Close) does
    // not ask it.
    private static IntPtr s_closeDelegate;

    private static unsafe IntPtr CloseDelegate()
    {
        if (s_closeDelegate != IntPtr.Zero) return s_closeDelegate;
        IntPtr cls = objc_getClass("WfFormWindowDelegate");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Cls("NSObject"), "WfFormWindowDelegate", UIntPtr.Zero);
            class_addMethod(cls, Sel("windowShouldClose:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&WindowShouldClose, "c@:@");
            class_addMethod(cls, Sel("windowWillResize:toSize:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, NSSize, NSSize>)&WindowWillResize, "{CGSize=dd}@:@{CGSize=dd}");
            objc_registerClassPair(cls);
        }
        s_closeDelegate = Send(Send(cls, Sel("alloc")), Sel("init"));
        return s_closeDelegate;
    }

    /// <summary>A step of AppKit's live resize: WM_SIZING, as Windows sends it at each step of the user's resize,
    /// and the size the window leaves in it. AppKit does not say which edge is dragged; the bottom-right is assumed.</summary>
    [UnmanagedCallersOnly]
    private static NSSize WindowWillResize(IntPtr self, IntPtr cmd, IntPtr window, NSSize size)
    {
        try
        {
            CocoaHost host = null;
            foreach (CocoaHost h in s_live) if (h._window == window) { host = h; break; }
            if (host == null || host._form.IsDisposed || !host._form.IsHandleCreated) return size;
            NSRect frame = SendRectRet(window, Sel("frame"));
            Rectangle form = host._form.Bounds;
            int fw = form.Width + (int)Math.Round((size.w - frame.w) * host._k), fh = form.Height + (int)Math.Round((size.h - frame.h) * host._k);
            Rectangle asked = host._driver.HostSizing(host._form.Handle, 8 /* WMSZ_BOTTOMRIGHT */, new Rectangle(form.X, form.Y, fw, fh));
            if (asked.Width <= 0 || asked.Height <= 0) return size;
            return new NSSize { w = size.w + (asked.Width - fw) / host._k, h = size.h + (asked.Height - fh) / host._k };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[CocoaHost] resize step: " + e.Message);
            return size;
        }
    }

    [UnmanagedCallersOnly]
    private static byte WindowShouldClose(IntPtr self, IntPtr cmd, IntPtr window)
    {
        // Inside AppKit: nothing may unwind through its frames.
        try
        {
            CocoaHost host = null;
            foreach (CocoaHost h in s_live) if (h._window == window) { host = h; break; }
            if (host == null || host._form.IsDisposed || !host._form.IsHandleCreated) return 1;   // not a form's
            const int SC_CLOSE = 0xF060;
            if (s_traceFrames) Console.WriteLine($"frame: '{host._form.Text}' close button: modal={host._form.Modal} enabled={host._form.Enabled} visible={host._form.Visible} active={Form.ActiveForm?.Text}");
            XplatUI.SendMessage(host._form.Handle, Msg.WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            if (s_traceFrames) Console.WriteLine($"frame: '{host._form.Text}' after close: visible={host._form.Visible} handle={host._form.IsHandleCreated} disposed={host._form.IsDisposed} windowVisible={(host._window != IntPtr.Zero && (nint)Send(host._window, Sel("isVisible")) != 0)}");
            if (!host._closed && host._window != IntPtr.Zero) host.Present();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[CocoaHost] close button: " + e.Message);
        }
        return 0;
    }

    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    // ---- the keyboard ----------------------------------------------------------------------
    //
    // What Windows gives a window for a key: WM_KEYDOWN with its virtual key -- which is where WinForms
    // moves the focus on Tab, presses the default button on Enter, the cancel button on Escape -- then
    // the character it types (TranslateMessage's WM_CHAR), unless pre-processing took the key, and
    // WM_KEYUP when it is let go, with the modifiers as they are. The keys went as characters alone:
    // Tab typed a tab and moved no focus, Enter and Escape pressed no button, and no shortcut existed.
    // Command counts as Control, as the browser head has it: the Mac's shortcuts are on it.
    private void HandleKey(IntPtr evt, nint type)
    {
        if (_driver is not XplatUIWebGpu d) return;
        // The keyboard's messages go to the focus window (GetFocus), whichever window AppKit made key:
        // with a hosted WPF window focused (an ElementHost's, clicked), the form is still the key window,
        // and the keys went to the form's WinForms controls -- Delete over app's display did nothing.
        IntPtr focus = WindowsBase::MS.Internal.Interop.PlatformFocus.GetFocus();
        if (focus != IntPtr.Zero && Control.ForeignChildOwner(focus) != null
            && (s_wpfKeyTo ??= AppContext.GetData("WpfWebGpu.CocoaWindow.DispatchKeyTo") as Action<IntPtr, IntPtr>) is { } keyTo)
        {
            if (KeyTrace.On) KeyTrace.Log($"cocoa key event to the focus window 0x{focus:x} (hosted)");
            keyTo(evt, focus);
            return;
        }
        ulong flags = (ulong)(nint)Send(evt, Sel("modifierFlags"));
        const ulong Shift = 1UL << 17, Ctrl = 1UL << 18, Option = 1UL << 19, Command = 1UL << 20;
        int keyCode = type == 12 ? -1 : (int)(nint)Send(evt, Sel("keyCode"));
        // Command+Delete (the Mac's delete, Windows' Backspace) is the Delete key: the Mac's gesture for
        // deleting an item, and the one way a Mac keyboard without a forward-delete key deletes a selected
        // control. Command is then not Control: the key is a plain Delete, not Ctrl+Delete.
        bool commandDelete = keyCode == 0x33 && (flags & Command) != 0;
        bool control = (flags & Ctrl) != 0 || (flags & Command) != 0 && !commandDelete;
        d.SetModifierKeys((control ? Keys.Control : 0) | ((flags & Shift) != 0 ? Keys.Shift : 0) | ((flags & Option) != 0 ? Keys.Alt : 0));
        if (type == 12) return;   // a modifier alone: the state is what changed

        int vk = commandDelete ? 0x2E /* VK_DELETE */ : VirtualKey(keyCode, Characters(evt, "charactersIgnoringModifiers"));
        if (type == 11)
        {
            if (vk != 0) d.InjectKeyUp(vk);
            return;
        }
        bool consumed = vk != 0 && d.InjectKeyDown(vk);
        if (!consumed)
        {
            // the control characters Windows translates these into; the typed text otherwise
            if (vk is 0x08 or 0x09 or 0x0D or 0x1B) d.InjectChar((char)vk);
            else if (!control)
                foreach (char c in Characters(evt, "characters"))
                    if (c >= ' ' && c != (char)0x7F && !(c >= (char)0xF700 && c <= (char)0xF8FF))
                        d.InjectChar(c);
        }
        Application.DoEvents();
        Present();
    }

    private static string Characters(IntPtr evt, string selector)
    {
        IntPtr chars = Send(evt, Sel(selector));
        return chars == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(chars, Sel("UTF8String"))) ?? "";
    }

    /// <summary>The Win32 virtual key for a Mac key: a letter or digit by what it types in the current
    /// layout (Windows' VK_A..VK_Z and VK_0..VK_9 follow the layout too), every other key by its place
    /// (kVK_*).</summary>
    internal static int VirtualKey(int keyCode, string unmodified)
    {
        if (unmodified.Length == 1)
        {
            char c = char.ToUpperInvariant(unmodified[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }
        return keyCode switch
        {
            0x24 or 0x4C => 0x0D,   // Return, keypad Enter
            0x30 => 0x09,           // Tab
            0x31 => 0x20,           // Space
            0x33 => 0x08,           // Delete (backspace)
            0x35 => 0x1B,           // Escape
            0x75 => 0x2E,           // forward Delete
            0x73 => 0x24, 0x77 => 0x23, 0x74 => 0x21, 0x79 => 0x22,   // Home, End, PageUp, PageDown
            0x7B => 0x25, 0x7E => 0x26, 0x7C => 0x27, 0x7D => 0x28,   // Left, Up, Right, Down
            0x72 => 0x2D,           // Help (Insert on a PC keyboard)
            0x7A => 0x70, 0x78 => 0x71, 0x63 => 0x72, 0x76 => 0x73, 0x60 => 0x74, 0x61 => 0x75,   // F1..F6
            0x62 => 0x76, 0x64 => 0x77, 0x65 => 0x78, 0x6D => 0x79, 0x67 => 0x7A, 0x6F => 0x7B,   // F7..F12
            0x52 => 0x60, 0x53 => 0x61, 0x54 => 0x62, 0x55 => 0x63, 0x56 => 0x64,                 // keypad 0..4
            0x57 => 0x65, 0x58 => 0x66, 0x59 => 0x67, 0x5B => 0x68, 0x5C => 0x69,                 // keypad 5..9
            0x43 => 0x6A, 0x45 => 0x6B, 0x4E => 0x6D, 0x41 => 0x6E, 0x4B => 0x6F,                 // keypad * + - . /
            0x1B => 0xBD, 0x18 => 0xBB, 0x21 => 0xDB, 0x1E => 0xDD, 0x2A => 0xDC,                 // - = [ ] \
            0x29 => 0xBA, 0x27 => 0xDE, 0x2B => 0xBC, 0x2F => 0xBE, 0x2C => 0xBF, 0x32 => 0xC0,   // ; ' , . / `
            _ => 0,
        };
    }

    // ---- the cursor ------------------------------------------------------------------------
    //
    // What the control under the pointer asks for (the driver's StdCursor: Control.Cursor, a
    // Cursor.Current), as Windows shows it on WM_SETCURSOR. Nothing here set one, and the pointer was
    // an arrow over every splitter, text box and link. Public NSCursor API only: AppKit's private
    // cursors (_moveCursor and the like) answer respondsToSelector and then throw an Objective-C
    // exception when called on some systems, which aborted the app. The diagonal sizes are macOS 15's
    // frame-resize cursors where there are any; a move is the open hand; help and wait (which macOS
    // shows by itself for a busy app) the arrow.
    private static int s_cursorShown = int.MinValue;
    private static readonly bool s_traceCursor = Environment.GetEnvironmentVariable("WF_TRACE_CURSOR") == "1";

    // NSCursorFrameResizePosition and NSCursorFrameResizeDirections (macOS 15)
    private const nuint FrameTop = 1, FrameLeft = 2, FrameBottom = 4, FrameRight = 8, FrameAllDirections = 3;

    private void ApplyCursor()
    {
        if (_form.IsDisposed || _form.Disposing || !_form.IsHandleCreated)
            return;
        // over a WPF window embedded in the form (an ElementHost's child window) the cursor is WPF's: on Windows the
        // child window under the pointer answers WM_SETCURSOR. Set here as well, the form's -- the ElementHost's
        // default arrow -- replaced WPF's on every move, and a display's resize handles never showed theirs.
        // A popup of the form's over that window (a context menu over a display) is what is under the
        // pointer, and takes WM_SETCURSOR there: WPF's cursor stayed over the menu, a move cursor
        // of the control the menu was opened on.
        if (PointerOverEmbeddedWindow() && !_driver.PointerOverOtherTopLevel(_form.Handle))
        {
            if (s_traceCursor && s_cursorShown != -2) Console.WriteLine($"[cocoa cursor] '{_form.Text}': over a hosted WPF window, left to it");
            s_cursorShown = -2;
            return;
        }
        int id = _driver.GetActiveCursor();
        // set on every move, not only when it changes: an embedded WPF window sets its own over itself
        if (s_traceCursor && id != s_cursorShown) Console.WriteLine($"[cocoa cursor] '{_form.Text}': StdCursor {id} from {_driver.DescribeActiveCursor()}");
        s_cursorShown = id;
        IntPtr cursor = id switch
        {
            3 => Public("crosshairCursor"),                                     // Cross
            4 => Public("pointingHandCursor"),                                  // Hand
            6 or 22 => Public("resizeUpDownCursor"),                            // HSplit, SizeNS
            7 => Public("IBeamCursor"),                                         // IBeam
            8 => Public("operationNotAllowedCursor"),                           // No
            >= 9 and <= 20 => Public("openHandCursor"),                         // NoMove*, Pan*, SizeAll
            21 => FrameResize(FrameTop | FrameRight),                           // SizeNESW
            23 => FrameResize(FrameTop | FrameLeft),                            // SizeNWSE
            24 or 26 => Public("resizeLeftRightCursor"),                        // SizeWE, VSplit
            _ => IntPtr.Zero,                                                   // Default, Arrow, Help, Wait, UpArrow
        };
        if (id >= XplatUIWebGpu.FirstCustomCursor) cursor = CustomCursor(id);
        if (cursor == IntPtr.Zero) cursor = Public("arrowCursor");
        if (cursor != IntPtr.Zero) Send(cursor, Sel("set"));
    }

    private static readonly System.Collections.Generic.Dictionary<int, IntPtr> s_customCursors = new();

    /// <summary>An application's own cursor (from a .cur file or resource) as an NSCursor: its picture, a
    /// point a pixel as Windows shows it at 100 %, and its hotspot.</summary>
    private static IntPtr CustomCursor(int id)
    {
        if (s_customCursors.TryGetValue(id, out IntPtr made)) return made;
        IntPtr cursor = IntPtr.Zero;
        try
        {
            if (XplatUIWebGpu.TryGetCustomCursor(id, out System.Drawing.Bitmap image, out System.Drawing.Point hot))
            {
                using var png = new System.IO.MemoryStream();
                image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                byte[] bytes = png.ToArray();
                IntPtr data = SendDataBytes(Cls("NSData"), Sel("dataWithBytes:length:"), bytes, (nuint)bytes.Length);
                IntPtr nsImage = data == IntPtr.Zero ? IntPtr.Zero : SendPtr(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
                if (nsImage != IntPtr.Zero)
                {
                    SendVoidSize(nsImage, Sel("setSize:"), new NSSize { w = image.Width, h = image.Height });
                    cursor = SendCursorInit(Send(Cls("NSCursor"), Sel("alloc")), Sel("initWithImage:hotSpot:"), nsImage, new NSPoint { x = hot.X, y = hot.Y });
                }
            }
        }
        catch (Exception e) { if (s_traceCursor) Console.WriteLine($"[cocoa cursor] custom {id}: {e.Message}"); }
        s_customCursors[id] = cursor;
        return cursor;
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDataBytes(IntPtr r, IntPtr s, byte[] bytes, nuint length);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidSize(IntPtr r, IntPtr s, NSSize size);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendCursorInit(IntPtr r, IntPtr s, IntPtr image, NSPoint hotSpot);

    private static IntPtr Public(string name) => Send(Cls("NSCursor"), Sel(name));

    // ---- tray icons (NotifyIcon) ----------------------------------------------------------
    //
    // A NotifyIcon is a status item in the menu bar, its icon there and its text the tool tip. A click is told to
    // the icon's window as the Windows shell tells it, WM_USER with the mouse message in lParam: a press and a
    // release, a double click, a right click (whose release puts up the icon's context menu).

    private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> s_trayItems = new();    // window -> NSStatusItem
    private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> s_trayButtons = new();  // button -> window
    private static IntPtr s_trayTarget;

    private static unsafe IntPtr TrayTarget()
    {
        if (s_trayTarget != IntPtr.Zero) return s_trayTarget;
        IntPtr cls = objc_getClass("WfTrayTarget");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Cls("NSObject"), "WfTrayTarget", UIntPtr.Zero);
            class_addMethod(cls, Sel("clicked:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&TrayClicked, "v@:@");
            objc_registerClassPair(cls);
        }
        s_trayTarget = Send(Send(cls, Sel("alloc")), Sel("init"));
        return s_trayTarget;
    }

    // ---- balloon tips (NotifyIcon.ShowBalloonTip) ----------------------------------------------------
    //
    // A notification of the notification centre (AppleNotifications), as Windows 10 shows a balloon as a toast -- for an
    // application that is a bundle, the only kind the centre accepts. A process that is no bundle (an application run
    // straight from its folder) gets the balloon the theme draws, under the menu bar where its icon is.

    private static bool? s_bundled;

    private static void MacBalloon(IntPtr window, int timeout, string title, string text, ToolTipIcon icon)
    {
        bool bundled;
        try { bundled = s_bundled ??= OnMainResult(AppleNotifications.Available); } catch (Exception) { bundled = false; }
        if (!bundled)
        {
            ThemeEngine.Current.ShowBalloonWindow(window, timeout, title, text, icon);
            XplatUI.PostMessage(window, (Msg)0x0400, IntPtr.Zero, (IntPtr)AppleNotifications.NIN_BALLOONSHOW);
            return;
        }
        OnMain(() => AppleNotifications.Show(window, title ?? "", text ?? ""));
    }

    private static T OnMainResult<T>(Func<T> f)
    {
        T result = default;
        if (pthread_main_np() != 0) return f();
        var done = new System.Threading.ManualResetEventSlim();
        OnMain(() => { try { result = f(); } finally { done.Set(); } });
        done.Wait(2000);
        return result;
    }

    private static void TrayAdd(IntPtr window, string tip, System.Drawing.Icon icon)
    {
        if (s_trayItems.ContainsKey(window)) { TrayChange(window, tip, icon); return; }
        IntPtr bar = Send(Cls("NSStatusBar"), Sel("systemStatusBar"));
        IntPtr item = SendPtrDouble(bar, Sel("statusItemWithLength:"), -1.0);   // NSVariableStatusItemLength
        if (item == IntPtr.Zero) return;
        Send(item, Sel("retain"));
        IntPtr button = Send(item, Sel("button"));
        s_trayItems[window] = item;
        if (button != IntPtr.Zero)
        {
            s_trayButtons[button] = window;
            SendVoidPtr(button, Sel("setTarget:"), TrayTarget());
            SendVoidPtr(button, Sel("setAction:"), Sel("clicked:"));
            // the release of either button acts, as the shell reports both
            Send(button, Sel("sendActionOn:"), (IntPtr)((1 << 2) | (1 << 4)));   // NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp
        }
        TrayChange(window, tip, icon);
    }

    private static void TrayChange(IntPtr window, string tip, System.Drawing.Icon icon)
    {
        if (!s_trayItems.TryGetValue(window, out IntPtr item)) return;
        IntPtr button = Send(item, Sel("button"));
        if (button == IntPtr.Zero) return;
        SendVoidPtr(button, Sel("setToolTip:"), string.IsNullOrEmpty(tip) ? IntPtr.Zero : NSStr(tip));
        if (icon == null) return;
        try
        {
            using var small = new System.Drawing.Icon(icon, 32, 32);
            using System.Drawing.Bitmap bmp = small.ToBitmap();
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] png = ms.ToArray();
            IntPtr data = SendDataBytes(Cls("NSData"), Sel("dataWithBytes:length:"), png, (nuint)png.Length);
            IntPtr image = data == IntPtr.Zero ? IntPtr.Zero : SendPtr(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            SendVoidSize(image, Sel("setSize:"), new NSSize { w = 18, h = 18 });   // the menu bar's icon size
            SendVoidPtr(button, Sel("setImage:"), image);
        }
        catch (Exception e) { if (s_traceFrames) Console.WriteLine("tray icon: " + e.Message); }
    }

    private static void TrayRemove(IntPtr window)
    {
        if (!s_trayItems.Remove(window, out IntPtr item)) return;
        IntPtr button = Send(item, Sel("button"));
        if (button != IntPtr.Zero) s_trayButtons.Remove(button);
        SendVoidPtr(Send(Cls("NSStatusBar"), Sel("systemStatusBar")), Sel("removeStatusItem:"), item);
        Send(item, Sel("release"));
    }

    [UnmanagedCallersOnly]
    private static void TrayClicked(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            if (!s_trayButtons.TryGetValue(sender, out IntPtr window)) return;
            IntPtr evt = Send(App(), Sel("currentEvent"));
            nint type = evt == IntPtr.Zero ? 2 : (nint)Send(evt, Sel("type"));
            nint clicks = evt == IntPtr.Zero ? 1 : (nint)Send(evt, Sel("clickCount"));
            const int WM_USER = 0x0400, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
            bool right = type == 4 || (type == 2 && (((nuint)(nint)Send(evt, Sel("modifierFlags"))) & (1u << 18)) != 0);   // or Control-click
            int[] sequence = right ? new[] { WM_RBUTTONDOWN, WM_RBUTTONUP }
                : clicks >= 2 ? new[] { WM_LBUTTONDBLCLK, WM_LBUTTONUP } : new[] { WM_LBUTTONDOWN, WM_LBUTTONUP };
            foreach (int m in sequence) XplatUI.PostMessage(window, (Msg)WM_USER, IntPtr.Zero, (IntPtr)m);
        }
        catch (Exception e) { Console.Error.WriteLine("[CocoaHost] tray click: " + e.Message); }
    }

    // ---- the form's icon ------------------------------------------------------------------
    //
    // Windows shows the main window's icon in the taskbar; a Mac shows the application's in the Dock. An
    // application run without a bundle icon of its own (through the launcher) showed a generic one there, its
    // forms' icons nowhere. The icon of the application's first form in the taskbar (ShowInTaskbar, no owner) is
    // the Dock's, unless the bundle has one.

    private static IntPtr s_dockIconForm;

    private static void ShowFormIcon(IntPtr handle, System.Drawing.Icon icon)
    {
        if (icon == null || Control.FromHandle(handle) is not Form form || !form.ShowInTaskbar || form.Owner != null || form.TopLevel == false) return;
        if (s_dockIconForm != IntPtr.Zero && s_dockIconForm != handle && Control.FromHandle(s_dockIconForm) is Form { IsDisposed: false }) return;
        s_dockIconForm = handle;
        byte[] png;
        try
        {
            using var big = new System.Drawing.Icon(icon, 256, 256);
            using System.Drawing.Bitmap bmp = big.ToBitmap();
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            png = ms.ToArray();
        }
        catch (Exception) { return; }
        OnMain(() =>
        {
            IntPtr bundle = Send(Cls("NSBundle"), Sel("mainBundle"));
            if (bundle != IntPtr.Zero && SendPtr(bundle, Sel("objectForInfoDictionaryKey:"), NSStr("CFBundleIconFile")) != IntPtr.Zero) return;
            IntPtr data = SendDataBytes(Cls("NSData"), Sel("dataWithBytes:length:"), png, (nuint)png.Length);
            IntPtr image = data == IntPtr.Zero ? IntPtr.Zero : SendPtr(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            if (image != IntPtr.Zero) SendVoidPtr(App(), Sel("setApplicationIconImage:"), image);
        });
    }

    private bool PointerOverEmbeddedWindow()
    {
        if (_window == IntPtr.Zero) return false;
        IntPtr children = Send(_window, Sel("childWindows"));
        nint count = children == IntPtr.Zero ? 0 : (nint)Send(children, Sel("count"));
        if (count == 0) return false;
        NSPoint mouse = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));   // the screen's, as window frames are
        for (nint i = 0; i < count; i++)
        {
            IntPtr child = SendPtrNUInt(children, Sel("objectAtIndex:"), (nuint)i);
            if (child == _popupWindow || (nint)Send(child, Sel("isVisible")) == 0) continue;
            NSRect f = SendRectRet(child, Sel("frame"));
            if (mouse.x >= f.x && mouse.x < f.x + f.w && mouse.y >= f.y && mouse.y < f.y + f.h) return true;
        }
        return false;
    }

    private static IntPtr FrameResize(nuint position)
    {
        IntPtr cls = Cls("NSCursor"), sel = Sel("frameResizeCursorFromPosition:inDirections:");
        return class_respondsToSelector(object_getClass(cls), sel) ? SendNUIntNUInt(cls, sel, position, FrameAllDirections) : IntPtr.Zero;
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNUIntNUInt(IntPtr r, IntPtr s, nuint a, nuint b);
    [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_respondsToSelector(IntPtr cls, IntPtr sel);

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
    [StructLayout(LayoutKind.Sequential)] private struct NSSize { public double w, h; }

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
