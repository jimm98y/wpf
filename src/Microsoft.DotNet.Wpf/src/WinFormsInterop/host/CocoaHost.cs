// Minimal on-screen host for the WinForms port on macOS: an NSWindow whose content is an
// NSImageView showing the driver's composited window bitmap, with a poll loop that drains
// NSEvents and routes left-clicks back through the driver (hit-test + WM_LBUTTON dispatch).
// Objective-C runtime P/Invoke, same style as the WPF fork's CocoaWindow. Presentation is a
// CoreGraphics bitmap for now; a WebGPU-texture path is the later swap.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
    {
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var list = new System.Collections.Generic.List<(object, int, int)>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (scene != null) list.Add((scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy));
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

    // ---- window + present -------------------------------------------------------

    public void Show()
    {
        if (_offMain)
        {
            double fw = _form.Width, fh = _form.Height;
            int fx = _form.Left, fy = _form.Top;
            string title = _form.Text ?? "WinForms";
            OnMain(() =>
            {
                if (_closed) return;
                EnsureApp();
                IntPtr nw = SendInitWindow(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                    CocoaRect(fx, fy, fw, fh), (nuint)15, (nuint)2, false);
                SendVoidPtr(nw, Sel("setTitle:"), NSStr(title));
                IntPtr nv = SendInitRect(Send(Cls("NSView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = fw, h = fh });
                SendVoidBool(nv, Sel("setWantsLayer:"), true);
                SendVoidPtr(nw, Sel("setContentView:"), nv);
                _imageView = nv;
                _window = nw;
                SendVoidPtr(nw, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
                if (UseWebGpu)
                {
                    try
                    {
                        var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
                        IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, nv);
                        _pendingScale = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.MacInterop.BackingScale(nv);
                        _pendingContext = ctx;
                        _pendingSurface = surface;
                    }
                    catch (Exception e) { Console.Error.WriteLine("[CocoaHost] WebGPU surface for an off-main form failed: " + e.Message); }
                }
            });
            Present();
            return;
        }

        EnsureApp();
        double w = _form.Width, h = _form.Height;
        IntPtr win = Send(Cls("NSWindow"), Sel("alloc"));
        var rect = CocoaRect(_form.Left, _form.Top, w, h);
        // styleMask: Titled(1)|Closable(2)|Miniaturizable(4)|Resizable(8) = 15; backing Buffered(2).
        win = SendInitWindow(win, Sel("initWithContentRect:styleMask:backing:defer:"), rect, (nuint)15, (nuint)2, false);
        SendVoidPtr(win, Sel("setTitle:"), NSStr(_form.Text ?? "WinForms"));

        IntPtr view = Send(Cls("NSView"), Sel("alloc"));
        view = SendInitRect(view, Sel("initWithFrame:"), new NSRect { x = 0, y = 0, w = w, h = h });
        SendVoidBool(view, Sel("setWantsLayer:"), true);   // layer-backed: we set layer.contents = CGImage
        SendVoidPtr(win, Sel("setContentView:"), view);

        _window = win; _imageView = view;
        // What the form asked for. AppKit may constrain it (below the menu bar, onto the screen);
        // the first Pump then hands the real frame back to the form.
        _knownBounds = new Rectangle(_form.Left, _form.Top, _form.Width, _form.Height);
        SendVoidPtr(win, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        Send(App(), Sel("activateIgnoringOtherApps:"), (IntPtr)1);

        // WebGPU present path (on unless WF_WEBGPU=0): create a wgpu context, then a CAMetalLayer surface over
        // this NSView (the mac branch of the cross-platform surface abstraction; Windows would use an
        // HWND source, the browser a canvas), then present each composite as a textured quad.
        if (UseWebGpu)
        {
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
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
            _wgpu = new WgpuPresenter(ctx, surface, _form.Width, _form.Height, scale, srgb: _gpuRaster);
            if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
                Console.Error.WriteLine($"WebGPU present path active (surface 0x{surface:x}, format {_wgpu.Format}, scale {scale})");
            // Let embedded non-WinForms content (an ElementHost's WPF tree) reach the real window and
            // its backing scale now that both exist. Inert when nothing is embedded.
            EmbeddedScenes.PublishHostWindow(_imageView, (float)scale);
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

            var scenes = GetScenes(out int ox, out int oy);
            Rectangle? caret = GetCaretRect(ox, oy);
            _lastPresentOk = _wgpu.PresentScenes(scenes, caret, _form.Width, _form.Height);
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
                _wgpu = new WgpuPresenter(_pendingContext, _pendingSurface, _form.Width, _form.Height, _pendingScale, srgb: _gpuRaster);
                _lastVer = -1;
            }
            if (_wgpu != null && _gpuRaster && !_closed)
            {
                int v = (int)_getVersion.Invoke(_driver, null);
                bool on = CaretOn();
                if (v == _lastVer && on == _lastCaretOn && _lastPresentOk) return;
                var offScenes = GetScenes(out int sx, out int sy);
                _lastPresentOk = _wgpu.PresentScenes(offScenes, GetCaretRect(sx, sy), _form.Width, _form.Height);
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

    // The off-main-thread present: composite here (System.Drawing and CoreGraphics bitmaps are thread
    // safe), then set the layer's contents on the main thread. Only when the content changed: the
    // driver's loop calls Present on every iteration, and each one would otherwise be a main-queue hop.
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
        OnMain(() =>
        {
            IntPtr view = _imageView;
            if (view != IntPtr.Zero && !_closed)
            {
                SendVoidPtr(Send(view, Sel("layer")), Sel("setContents:"), cg);
                Send(Cls("CATransaction"), Sel("flush"));
            }
            CGImageRelease(cg);
        });
    }

    // ---- the window frame and the form's bounds -----------------------------------------
    //
    // On Windows the form's bounds ARE the window's: the user dragging or resizing it is a WM_MOVE /
    // WM_SIZE, and Form.Location/Size set from code moves the window. Here they are two things kept
    // in step: a change on either side since the last agreement is applied to the other. The user
    // side has to reach the form for anything that follows the window -- layout (a docked property
    // grid at the right edge), Move/Resize handlers, an ElementHost's own window, screen coordinates.

    private Rectangle _knownBounds;

    private void SyncFrame()
    {
        if (_window == IntPtr.Zero || _form.IsDisposed) return;
        // A minimized window keeps its frame; the form's restore bounds are not this.
        if ((nint)Send(_window, Sel("isMiniaturized")) != 0) return;

        Rectangle native = NativeBounds();
        if (native.Width <= 0 || native.Height <= 0) return;
        if (native != _knownBounds)
        {
            // Moved or resized by the user (or by AppKit placing it): the form follows.
            _knownBounds = native;
            if (_form.Bounds != native) _form.Bounds = native;
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

    /// <summary>Runs <paramref name="action"/> on the main thread: now if this is it, else queued.</summary>
    private static unsafe void OnMain(Action action)
    {
        if (pthread_main_np() != 0) { action(); return; }
        if (s_mainQueue == IntPtr.Zero)
        {
            // dispatch_get_main_queue() is a macro for &_dispatch_main_q.
            s_mainQueue = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.dylib"), "_dispatch_main_q");
        }
        GCHandle handle = GCHandle.Alloc(action);
        dispatch_async_f(s_mainQueue, GCHandle.ToIntPtr(handle), (IntPtr)(delegate* unmanaged<IntPtr, void>)&RunQueued);
    }

    [UnmanagedCallersOnly]
    private static void RunQueued(IntPtr context)
    {
        GCHandle handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target;
        handle.Free();
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[CocoaHost] main-queue work failed: {e}"); }
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
        IntPtr win = _window;
        _window = IntPtr.Zero;
        Send(win, Sel("close"));
    }

    public bool Pump()
    {
        // Events are the main thread's to read; see _offMain.
        if (_offMain) return !_closed && _form.Visible && !_form.IsDisposed;

        SyncFrame();

        while (true)
        {
            IntPtr evt = SendNextEvent(App(), Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                unchecked((nuint)ulong.MaxValue), IntPtr.Zero, NSStr("kCFRunLoopDefaultMode"), true);
            if (evt == IntPtr.Zero) break;

            nint type = (nint)Send(evt, Sel("type"));
            // NSEventType: LeftMouseDown=1, LeftMouseUp=2, RightMouseDown=3, RightMouseUp=4,
            // MouseMoved=5, LeftMouseDragged=6.
            if (type >= 1 && type <= 6)
            {
                NSPoint loc = SendPointRet(evt, Sel("locationInWindow"));
                int cx = (int)loc.x;
                int cy = (int)(_form.Height - loc.y);   // Cocoa is bottom-left origin; WinForms top-left
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
                continue;   // don't forward key events to AppKit (we've handled them)
            }
            Send(App(), Sel("sendEvent:"), evt);
        }
        return (nint)Send(_window, Sel("isVisible")) != 0;
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

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CG)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] private static extern void CGColorSpaceRelease(IntPtr cs);
    [DllImport(CG)] private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint w, nuint h, nuint bpc, nuint bpr, IntPtr cs, uint info);
    [DllImport(CG)] private static extern IntPtr CGBitmapContextCreateImage(IntPtr ctx);
    [DllImport(CG)] private static extern void CGContextRelease(IntPtr ctx);
    [DllImport(CG)] private static extern void CGImageRelease(IntPtr img);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint SendPointRet(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNextEvent(IntPtr r, IntPtr s, nuint mask, IntPtr date, IntPtr mode, [MarshalAs(UnmanagedType.I1)] bool dequeue);
}
