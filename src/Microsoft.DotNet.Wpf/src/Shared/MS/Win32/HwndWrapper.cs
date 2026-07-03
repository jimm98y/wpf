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
                    bool borderless = (style & WS_CHILD) != 0 || ((uint)style & WS_POPUP) != 0;

                    int cw = width > 0 ? width : (borderless ? 1 : 1024);
                    int ch = height > 0 ? height : (borderless ? 1 : 768);
                    int cx = x > 0 ? x : 100;
                    int cy = y > 0 ? y : 100;
                    _cocoaWindow = new MS.Internal.Interop.CocoaWindow();
                    _cocoaWindow.Create(name, cx, cy, cw, ch, borderless);
                    _handle = _cocoaWindow.ContentView;
                    // Register the handle so off-Windows SendMessage (e.g. Window.Close()'s WM_CLOSE)
                    // can be routed synchronously to this wrapper's managed WndProc (see DispatchMessage).
                    lock (s_byHandleLock) { s_byHandle[_handle] = this; }
                    // Route Cocoa content-size changes to a synthetic WM_SIZE so the registered hooks
                    // (HwndTarget re-render + HwndSource re-layout) run exactly as on Windows.
                    _cocoaWindow.Resized += OnCocoaResized;
                    // Route a title-bar close-button click to a WM_CLOSE (which fires Closing/Closed
                    // and tears the window down, exactly as the Win32 close path does).
                    _cocoaWindow.Closed += OnCocoaClosed;
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
                }
                _cocoaWindow?.Destroy();
                _cocoaWindow = null;
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
        private MS.Internal.Interop.CocoaWindow _cocoaWindow;
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

