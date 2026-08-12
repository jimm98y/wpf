// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MS.Internal;
using MS.Internal.Interop;

namespace MS.Win32
{
    internal class HwndWrapper : DispatcherObject, IDisposable
    {
        static HwndWrapper()
        {
            s_msgGCMemory = UnsafeNativeMethods.RegisterWindowMessage("HwndWrapper.GetGCMemMessage");
        }

        public HwndWrapper(
            int classStyle,
            int style,
            int exStyle,
            int x,
            int y,
            int width,
            int height,
            string name,
            IntPtr parent,
            HwndWrapperHook[] hooks)
        {
            _ownerThreadID = Environment.CurrentManagedThreadId;


            // First, add the set of hooks.  This allows the hooks to receive the
            // messages sent to the window very early in the process.
            if(hooks != null)
            {
                for(int i = 0, iEnd = hooks.Length; i < iEnd; i++)
                {
                    if(null != hooks[i])
                        AddHook(hooks[i]);
                }
            }


            _wndProc = new HwndWrapperHook(WndProc);

            // Off-Windows there is no Win32 window class/HWND. A sized request maps to a real Cocoa
            // NSWindow (its layer-backed NSView* becomes the handle the compositor targets); an
            // unsized request (parking / message-only window) just gets a unique synthetic handle
            // since nothing dereferences it once the Win32 message paths are gone.
            if (!OperatingSystem.IsWindows())
            {
                // A styled window (style != 0) is a real top-level window -> back it with an
                // NSWindow whose NSView* is the handle. Parking / message-only windows (style 0)
                // just get a synthetic handle. WPF creates top-level windows with CW_USEDEFAULT
                // geometry and sizes them afterward via SetWindowPos, so substitute a default size
                // here when the requested one is not positive.
                if (style != 0)
                {
                    // Popups/child windows (WS_POPUP / WS_CHILD) are borderless floating windows (combo
                    // dropdowns, menus, tooltips); everything else is a normal titled top-level window.
                    const int WS_CHILD = 0x40000000;
                    const uint WS_POPUP = 0x80000000;
                    // WS_EX_LAYERED counts as borderless too, and is a different kind of window: a real
                    // top-level WPF Window that set AllowsTransparency. WPF asks for per-pixel alpha
                    // through the EXTENDED style, so reading only `style` classified these as ordinary
                    // titled windows and they came up opaque, with a title bar. Every drag adorner is
                    // built this way, so a docking drag showed blank grey boxes instead of adorners:
                    // only a borderless window gets the clear-background, no-shadow, non-activating
                    // treatment the popups already rely on, which is exactly what an adorner wants.
                    const int WS_EX_LAYERED = 0x00080000;
                    bool borderless = (style & WS_CHILD) != 0 || ((uint)style & WS_POPUP) != 0
                                   || (exStyle & WS_EX_LAYERED) != 0;

                    // CHROMELESS is the other half of "no native title bar", and deliberately NOT the
                    // same thing as borderless. Window.CreateWindowStyle clears WS_CAPTION for exactly
                    // one WPF setting -- WindowStyle=None -- meaning "no title bar, I draw my own
                    // chrome". A floating tool window is the everyday case, and it came up with a
                    // macOS title bar stacked above the caption it had drawn itself.
                    //
                    // It must not simply be folded into `borderless`, because these two want opposite
                    // things from AppKit. A borderless NSWindow answers NO to canBecomeKeyWindow, which
                    // is right for a menu or an adorner (they must never steal focus) and fatal for a
                    // real window: it could never take keyboard input, and would look frozen. So this
                    // is passed separately, and the backend keeps the window titled-but-chromeless.
                    const int WS_CAPTION = 0x00C00000;
                    bool chromeless = !borderless && (style & WS_CAPTION) == 0;

                    int cw = width > 0 ? width : (borderless ? 1 : 1024);
                    int ch = height > 0 ? height : (borderless ? 1 : 768);
                    // A borderless popup is given a real screen position (device pixels), which is
                    // legitimately NEGATIVE when it belongs to a display arranged to the left of / above
                    // the primary monitor. Preserve it verbatim -- do NOT treat x<=0 as "unspecified", or
                    // the popup gets yanked to the default (100) on the primary monitor. Only top-level
                    // windows (created at CW_USEDEFAULT-ish coordinates) get the default substitution.
                    //
                    // CW_USEDEFAULT itself is the exception, and must never be forwarded as if it were a
                    // position: it is int.MinValue, and AppKit rejects a frame built from it outright --
                    // "Invalid parameter not satisfying: CGRectContainsRect(...)", raised as an
                    // Objective-C exception that unwinds through managed frames and terminates the
                    // process. A WPF Window that never set Left/Top (every docking adorner) is created
                    // exactly this way, so starting a drag killed the app.
                    const int CW_USEDEFAULT = unchecked((int)0x80000000);
                    int cx = borderless && x != CW_USEDEFAULT ? x : (x > 0 ? x : 100);
                    int cy = borderless && y != CW_USEDEFAULT ? y : (y > 0 ? y : 100);
                    if (OperatingSystem.IsBrowser())
                    {
                        // Browser: the window is a canvas element (see BrowserWindow). Popups are
                        // positioned at their CreateWindowEx coordinates — WPF may skip the follow-up
                        // SetWindowPos move when the window is created where it already wants it
                        // (a reopened combo dropdown at the same spot), so creation must place it.
                        var browser = new MS.Internal.Interop.BrowserWindow();
                        browser.Create(name, cx, cy, cw, ch, borderless);
                        // Route content-size changes to a synthetic WM_SIZE exactly like Cocoa below.
                        browser.Resized += OnCocoaResized;
                        _platformWindow = browser;
                        _handle = browser.Handle;
                    }
                    else if (OperatingSystem.IsIOS())
                    {
                        // iOS: one fullscreen UIWindow, so a WPF window is a Metal-backed UIView
                        // inside it and popups are borderless sibling subviews (no child windows).
                        // The UIView* is the handle, exactly as Cocoa uses its NSView*.
                        // Checked BEFORE the macOS branch below: both are Darwin.
                        var uikit = new MS.Internal.Interop.UIKitWindow();
                        uikit.Create(name, cx, cy, cw, ch, borderless);
                        // Same synthetic WM_SIZE routing as the other two backends.
                        uikit.Resized += OnCocoaResized;
                        _platformWindow = uikit;
                        _handle = uikit.Handle;
                    }
                    else if (OperatingSystem.IsAndroid())
                    {
                        // Android: one fullscreen activity, so a WPF window is a view inside it and
                        // popups are borderless sibling views (no child windows), exactly as on iOS.
                        // The handle is SYNTHETIC here rather than a native pointer, because the
                        // Surface behind an Android view is destroyed and recreated across every
                        // activity stop/start while the WPF handle must stay put (see AndroidWindow).
                        var android = new MS.Internal.Interop.AndroidWindow();
                        android.Create(name, cx, cy, cw, ch, borderless);
                        // Same synthetic WM_SIZE routing as the other backends.
                        android.Resized += OnCocoaResized;
                        _platformWindow = android;
                        _handle = android.Handle;
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        // Linux/Wayland: libdecor owns the decorated toplevel and our own xdg_wm_base
                        // owns popups; the wl_surface* is the handle, exactly as the NSView* is on
                        // macOS. Checked BEFORE the Cocoa fallback below -- reaching that on Linux
                        // means P/Invoking libobjc, which is an immediate DllNotFoundException.
                        var wayland = new MS.Internal.Interop.Wayland.WaylandWindow();
                        wayland.Create(name, cx, cy, cw, ch, borderless, parent);
                        // Same synthetic WM_SIZE / WM_CLOSE routing as the other backends (the
                        // handlers are named for Cocoa but are entirely generic).
                        wayland.Resized += OnCocoaResized;
                        wayland.Closed += OnCocoaClosed;
                        _platformWindow = wayland;
                        _handle = wayland.Handle;
                    }
                    else
                    {
                        var cocoa = new MS.Internal.Interop.CocoaWindow();
                        // Pass the owner handle so a popup inherits its owner's display/backing scale
                        // (and therefore opens on the same monitor as the window it belongs to).
                        // WS_VISIBLE decides whether the window goes on screen NOW. WPF creates
                        // windows it means to keep hidden (docking adorners, cached popups) without it,
                        // and showing them anyway leaves WPF and the platform permanently disagreeing.
                        const int WS_VISIBLE = 0x10000000;
                        cocoa.Create(name, cx, cy, cw, ch, borderless, parent, chromeless,
                                     (style & WS_VISIBLE) != 0);
                        // Route Cocoa content-size changes to a synthetic WM_SIZE so the registered hooks
                        // (HwndTarget re-render + HwndSource re-layout) run exactly as on Windows.
                        cocoa.Resized += OnCocoaResized;
                        // Route a title-bar close-button click to a WM_CLOSE (which fires Closing/Closed
                        // and tears the window down, exactly as the Win32 close path does).
                        cocoa.Closed += OnCocoaClosed;
                        // Key-window changes become WM_ACTIVATE, without which WPF never learns the
                        // window is active: Window.IsActive stays false forever and every visual keyed
                        // off focus (a docking tab's selection border, for one) never lights up.
                        cocoa.ActiveChanged += OnCocoaActiveChanged;
                        _platformWindow = cocoa;
                        _handle = cocoa.ContentView;
                    }
                    // Register the handle so off-Windows SendMessage (e.g. Window.Close()'s WM_CLOSE)
                    // can be routed synchronously to this wrapper's managed WndProc (see DispatchMessage).
                    lock (s_byHandleLock) { s_byHandle[_handle] = this; }
                }
                else
                {
                    _handle = AllocateSyntheticHandle();
                }
                return;
            }

            // We create the HwndSubclass object so that we can use its
            // window proc directly.  We will not be "subclassing" the
            // window we create.
            HwndSubclass hwndSubclass = new(_wndProc);
            
            // Register a unique window class for this instance.
            NativeMethods.WNDCLASSEX_D wc_d = new NativeMethods.WNDCLASSEX_D();

            IntPtr hNullBrush = UnsafeNativeMethods.CriticalGetStockObject(NativeMethods.NULL_BRUSH);

            if (hNullBrush == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            IntPtr hInstance = UnsafeNativeMethods.GetModuleHandle( null );

            // We need to keep the Delegate object alive through the call to CreateWindowEx().
            // Subclass.WndProc will install a better delegate (to the same function) when it
            // processes the first message.
            // But this first delegate needs be held alive until then.
            NativeMethods.WndProc initialWndProc = new NativeMethods.WndProc(hwndSubclass.SubclassWndProc);

            // The class name is a concat of AppName, ThreadName, and RandomNumber.
            // Register will fail if the string gets over 255 in length.
            // So limit each part to a reasonable amount.
            string appName;
            string currentDomainFriendlyName = AppDomain.CurrentDomain.FriendlyName;
            if (null != currentDomainFriendlyName && 128 <= currentDomainFriendlyName.Length)
                appName = currentDomainFriendlyName[..128];
            else
                appName = currentDomainFriendlyName;

            string threadName;
            if(null != Thread.CurrentThread.Name && 64 <= Thread.CurrentThread.Name.Length)
                threadName = Thread.CurrentThread.Name.Substring(0, 64);
            else
                threadName = Thread.CurrentThread.Name;

            // Create a suitable unique class name.
            _classAtom = 0;
            string randomName = Guid.NewGuid().ToString();
            string className = String.Format(CultureInfo.InvariantCulture, "HwndWrapper[{0};{1};{2}]", appName, threadName, randomName);

            wc_d.cbSize        = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX_D));
            wc_d.style         = classStyle;
            wc_d.lpfnWndProc   = initialWndProc;
            wc_d.cbClsExtra    = 0;
            wc_d.cbWndExtra    = 0;
            wc_d.hInstance     = hInstance;
            wc_d.hIcon         = IntPtr.Zero;
            wc_d.hCursor       = IntPtr.Zero;
            wc_d.hbrBackground = hNullBrush;
            wc_d.lpszMenuName  = "";
            wc_d.lpszClassName = className;
            wc_d.hIconSm       = IntPtr.Zero;

            // Register the unique class for this instance.
            // Note we use a GUID in the name so we are confident that
            // the class name should be unique.  And RegisterClassEx won't
            // fail (for that reason).
            _classAtom = UnsafeNativeMethods.RegisterClassEx(wc_d);

            // call CreateWindow
            _isInCreateWindow = true;
            try {
                _handle = UnsafeNativeMethods.CreateWindowEx(exStyle,
                    className,
                    name,
                    style,
                    x,
                    y,
                    width,
                    height,
                    new HandleRef(null,parent),
                    new HandleRef(null,IntPtr.Zero),
                    new HandleRef(null,IntPtr.Zero),
                    null);
            }
            finally
            {
                _isInCreateWindow = false;
                if(_handle == 0)
                {
                    // Because the HwndSubclass is pinned, but the HWND creation failed,
                    // we need to manually clean it up.
                    hwndSubclass.Dispose();
                }
            }
            GC.KeepAlive(initialWndProc);
        }


        ~HwndWrapper()
        {
            Dispose(/*disposing = */ false, 
                    /*isHwndBeingDestroyed = */ false);
        }
        
        public virtual void Dispose()
        {
            //             VerifyAccess();

            Dispose(/*disposing = */ true, 
                    /*isHwndBeingDestroyed = */ false);
            GC.SuppressFinalize(this);
        }            

        // internal Dispose(bool, bool)
        private void Dispose(bool disposing, bool isHwndBeingDestroyed)
        {
            if (_isDisposed)
            {
                // protect against re-entrancy:  Calling DestroyWindow here will send
                // a WM_NCDESTROY -- WndProc may catch this and call Dispose again.
                return;
            }

            if(disposing)
            {
                // diposing == false means we're being called from the finalizer
                // and can't follow any reference types that may themselves be
                // finalizable - thus don't call the Disposed callback.

                // Notify listeners that we are being disposed.
                if(Disposed != null)
                {
                    Disposed(this, EventArgs.Empty);
                }
            }

            // We are now considered disposed.
            _isDisposed = true;

            // Off-Windows: tear down the Cocoa window (if any) and drop the handle; there is no
            // Win32 window/class to destroy or unregister.
            if (!OperatingSystem.IsWindows())
            {
                if (_handle != IntPtr.Zero)
                {
                    lock (s_byHandleLock) { s_byHandle.Remove(_handle); }

                    // A window that still holds the mouse capture is being destroyed. MouseCaptureHandle
                    // is this stack's stand-in for Win32 GetCapture(), and it is cleared only on an
                    // orderly release -- so a capturing window that is CLOSED instead left it pointing at
                    // a dead window forever. GetCapture() then reported capture that nothing could
                    // release, and WPF routed all subsequent mouse input to a window that no longer
                    // existed: every click went nowhere, app-wide. Closing a floating tool window at the
                    // end of a drag (docking it) does exactly this, which is why re-docking killed input.
                    // Win32 has no equivalent bug: DestroyWindow releases capture itself.
                    if (MS.Internal.Interop.PlatformWindow.MouseCaptureHandle == _handle)
                    {
                        MS.Internal.Interop.PlatformWindow.MouseCaptureHandle = IntPtr.Zero;
                    }
                }
                _platformWindow?.Destroy();
                _platformWindow = null;
                _handle = default;
                return;
            }

            if (isHwndBeingDestroyed)
            {
                // The window is in the process of being destroyed.  We can't call UnregisterClass yet
                // so we'll ask the Dispatcher to do it later when the window is gone.
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, (DispatcherOperationCallback)UnregisterClass, _classAtom);
            }
            else if (_handle != 0)
            {
                // The window isn't in the process of being destroyed and it hasn't been destroyed yet
                // (we know this since we're listening for WM_NCDESTROY).  Since we're being disposed
                // we destroy it now.

                if(Environment.CurrentManagedThreadId == _ownerThreadID)
                {
                    // We are the owner thread, we can safely destroy the window and unregister
                    // the class
                    DestroyWindow(new DestroyWindowArgs(_handle, _classAtom));
                }
                else
                {
                    // Post a DispatcherOperation to ask the owner thread to destroy the window for us.
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        (DispatcherOperationCallback)DestroyWindow,
                        new DestroyWindowArgs(_handle, _classAtom));
                }
            }

         
            _classAtom = 0;
            _handle = default;
        }

        public IntPtr Handle => _handle;

        public event EventHandler Disposed;

        public void AddHook(HwndWrapperHook hook)
        {
            _hooks ??= [];
            _hooks.Insert(0, hook);
        }

        internal void AddHookLast(HwndWrapperHook hook)
        {
            _hooks ??= [];
            _hooks.Add(hook);
        }

        public void RemoveHook(HwndWrapperHook hook) => _hooks?.Remove(hook);

        // Off-Windows registry: handle -> wrapper, so a "sent" Win32 message can be dispatched to the
        // right window's managed WndProc (there is no OS message queue to carry it).
        private static readonly System.Collections.Generic.Dictionary<IntPtr, HwndWrapper> s_byHandle = new();
        private static readonly object s_byHandleLock = new();

        /// <summary>
        /// Off-Windows stand-in for a synchronous SendMessage: runs the target window's managed WndProc
        /// (and its full hook chain) for <paramref name="msg"/>. This is what makes message-driven
        /// paths work without an OS message queue -- e.g. Window.Close() sending WM_CLOSE, which fires
        /// the Closing/Closed events and tears the window down.
        /// </summary>
        internal static IntPtr DispatchMessage(IntPtr handle, int msg, IntPtr wParam, IntPtr lParam)
        {
            HwndWrapper wrapper;
            lock (s_byHandleLock) { s_byHandle.TryGetValue(handle, out wrapper); }
            if (wrapper == null || wrapper._isDisposed) return IntPtr.Zero;

            bool handled = false;
            IntPtr result;
            try { result = wrapper.WndProc(handle, msg, wParam, lParam, ref handled); }
            catch { return IntPtr.Zero; }

            // Emulate DefWindowProc(WM_CLOSE): on Windows an unhandled WM_CLOSE destroys the window,
            // which generates WM_DESTROY (fires Window.Closed / app-shutdown bookkeeping) and then
            // disposes the wrapper (tearing down the Cocoa window). There is no DefWindowProc off-
            // Windows, so drive that sequence here when no hook claimed the close.
            const int WM_CLOSE = 0x0010;
            const int WM_DESTROY = 0x0002;
            if (msg == WM_CLOSE && !handled && !wrapper._isDisposed)
            {
                bool h = false;
                try { wrapper.WndProc(handle, WM_DESTROY, IntPtr.Zero, IntPtr.Zero, ref h); } catch { }
                wrapper.Dispose();
            }

            return result;
        }

        // The user clicked the title-bar close button: dispatch WM_CLOSE through the same path a
        // SendMessage would (including the DefWindowProc-style destroy when no hook cancels it).
        /// <summary>A Cocoa key-window change becomes the WM_ACTIVATE + WM_SETFOCUS / WM_KILLFOCUS
        /// sequence Win32 delivers, dispatched through the same hook chain as every other message.</summary>
        /// <remarks>
        /// Both messages are needed, and they answer different questions. WM_ACTIVATE is what
        /// Window.IsActive tracks; WM_SETFOCUS is what makes HwndSource restore keyboard focus into
        /// the element tree, which is what IsKeyboardFocusWithin — and therefore a focus-styled
        /// border — actually keys off.
        /// </remarks>
        private void OnCocoaActiveChanged(bool active)
        {
            if (_handle == IntPtr.Zero) return;
            const int WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
            const int WA_INACTIVE = 0, WA_ACTIVE = 1;
            bool handled = false;
            try
            {
                WndProc(_handle, WM_ACTIVATE, (IntPtr)(active ? WA_ACTIVE : WA_INACTIVE), IntPtr.Zero, ref handled);
                handled = false;
                WndProc(_handle, active ? WM_SETFOCUS : WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero, ref handled);
            }
            catch { }   // a callback exception must not break the Cocoa event pump that raised this
        }

        /// <summary>
        /// WM_SYSCOMMAND / SC_MOVE, i.e. "start dragging this window" — what Window.DragMove() and
        /// WindowChrome's caption drag both come down to. Win32 answers it with a modal move loop
        /// inside DefWindowProc; off-Windows the platform window runs its own.
        /// </summary>
        /// <remarks>
        /// Without this a window with app-drawn chrome cannot be moved at all. It only became
        /// reachable once the Cocoa backend stopped letting AppKit drag chromeless windows by their
        /// (invisible) title bar, since that was swallowing the clicks the app's menu bar needed.
        /// </remarks>
        private bool HandleSysCommandMove(IntPtr wParam)
        {
            const int SC_MOVE = 0xF010, SC_MOUSEMOVE = SC_MOVE + 0x02;
            int command = (int)wParam & 0xFFF0;
            if (command != SC_MOVE && ((int)wParam) != SC_MOUSEMOVE) return false;

            try { _platformWindow?.BeginMoveDrag(); } catch { }
            return true;
        }

        private void OnCocoaClosed(IntPtr handle)
        {
            const int WM_CLOSE = 0x0010;
            try { DispatchMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        // A Cocoa content-size change becomes a synthetic WM_SIZE dispatched through the same hook
        // chain the Win32 WndProc uses, so HwndTarget re-renders and HwndSource re-lays-out.
        private void OnCocoaResized(int width, int height)
        {
            if (_handle == IntPtr.Zero) return;
            const int WM_SIZE = 0x0005;
            IntPtr wParam = IntPtr.Zero;    // SIZE_RESTORED
            IntPtr lParam = (IntPtr)(((height & 0xFFFF) << 16) | (width & 0xFFFF));
            bool handled = false;
            // A callback exception must not break the Cocoa event pump that raised the resize.
            try { WndProc(_handle, WM_SIZE, wParam, lParam, ref handled); }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // The default result for messages we handle is 0.
            IntPtr result = IntPtr.Zero;
            WindowMessage message = (WindowMessage)msg;

            // Off-Windows there is no DefWindowProc to run the modal move loop this message asks for,
            // so answer it here before the hooks (none of them handle it, and an unhandled one would
            // simply be dropped, leaving the window immovable).
            const int WM_SYSCOMMAND = 0x0112;
            if (msg == WM_SYSCOMMAND && !OperatingSystem.IsWindows() && HandleSysCommandMove(wParam))
            {
                handled = true;
                return IntPtr.Zero;
            }


            // Call all of the hooks
            if(_hooks is not null)
            {
                foreach(HwndWrapperHook hook in _hooks)
                {
                    result = hook(hwnd, msg, wParam, lParam, ref handled);

                    CheckForCreateWindowFailure(result, handled);

                    if(handled)
                    {
                        break;
                    }
                }
            }

            if (message == WindowMessage.WM_NCDESTROY)
            {
                Dispose(/*disposing = */ true, 
                        /*isHwndBeingDestroyed = */ true);
                GC.SuppressFinalize(this);

                // We want the default window proc to process this message as
                // well, so we mark it as unhandled.
                handled = false;
            }
            else if (message == s_msgGCMemory)
            {
                // This is a special message we respond to by forcing a GC Collect.  This
                // is used by test apps and such.
                IntPtr lHeap = (IntPtr)GC.GetTotalMemory((wParam == new IntPtr(1) )? true : false);
                result =  lHeap;
                handled = true;
            }

            CheckForCreateWindowFailure(result, true);

            // return our result
            return result;
        }

        private void CheckForCreateWindowFailure( IntPtr result, bool handled )
        {
            if( ! _isInCreateWindow )
                return;
            
            if( IntPtr.Zero != result )
            {
                System.Diagnostics.Debug.WriteLine("Non-zero WndProc result=" + result);
                if( handled )
                {
                    if( System.Diagnostics.Debugger.IsAttached )
                        System.Diagnostics.Debugger.Break();
                    else
                        throw new InvalidOperationException();
                }
            }
        }


        /// <summary>
        /// Destroys the window with the given handle and class atom and unregisters its window class
        /// </summary>
        /// <param name="args">A DestrowWindowParams instance</param>
        internal static object DestroyWindow(object args)
        {
            nint handle = ((DestroyWindowArgs)args).Handle;
            ushort classAtom = ((DestroyWindowArgs)args).ClassAtom;

            Invariant.Assert(handle != 0,
               "Attempting to destroy an invalid hwnd");

            UnsafeNativeMethods.DestroyWindow(new HandleRef(null, handle));

            UnregisterClass((object)classAtom);

            return null;
        }

        /// <summary>
        /// Unregisters the window class represented by classAtom
        /// </summary>
        /// <param name="arg">A ushort representing the class atom</param>
        internal static object UnregisterClass(object arg)
        {
            ushort classAtom = (ushort)arg;

            if (classAtom != 0)
            {
                IntPtr hInstance = UnsafeNativeMethods.GetModuleHandle(null);
                UnsafeNativeMethods.UnregisterClass(
                                new IntPtr(classAtom), //* this function is defined as taking a type lpClassName - but this can be an atom. 2 Low Bytes are the atom*/ 
                                hInstance);
            }

            return null;
        }

        // This is used only so that DestroyWindow can take a single object parameter
        // in order for it to be called by a DispatcherOperationCallback
        internal class DestroyWindowArgs
        {
            public DestroyWindowArgs(IntPtr handle, ushort classAtom)
            {
                _handle = handle;
                _classAtom = classAtom;
            }

            public IntPtr Handle => _handle;

            public ushort ClassAtom => _classAtom;

            private readonly IntPtr _handle;
            private readonly ushort _classAtom;
        }
        

        private IntPtr _handle;
        private UInt16 _classAtom;
        private WeakReferenceList _hooks;
        private int _ownerThreadID;
        
        private HwndWrapperHook _wndProc;
        private bool _isDisposed;

        // Off-Windows backing: the Cocoa window (for sized/top-level windows) and a counter that
        // hands out unique non-zero synthetic handles for message-only/parking windows.
        private MS.Internal.Interop.IPlatformWindow _platformWindow;
        private static long s_syntheticHandle;

        private static IntPtr AllocateSyntheticHandle()
        {
            // Start well above 0 and step by a page so these never collide with each other or with
            // real NSView pointers; they are only ever compared for equality, never dereferenced.
            long value = System.Threading.Interlocked.Add(ref s_syntheticHandle, 0x1000) + 0x7F00_0000;
            return new IntPtr(value);
        }

        private bool _isInCreateWindow = false;     // debugging variable (temporary)

        // Message to cause a dispose.  We need this to ensure we destroy the window on the right thread.
        private static WindowMessage s_msgGCMemory;
    } // class RawWindow
}

