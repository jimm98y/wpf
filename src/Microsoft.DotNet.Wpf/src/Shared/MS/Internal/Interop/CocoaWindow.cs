// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Minimal Cocoa (AppKit) window backend used by HwndWrapper on macOS, where there is no
// Win32 HWND. It creates an NSWindow with a layer-backed NSView and exposes the NSView* as the
// window "handle" that the rest of WPF threads around in place of an HWND. The WebGPU compositor
// turns that NSView* into a wgpu Metal surface (see MacInterop.CreateSurface in the WebGPU
// engine), so a WPF Window maps to: NSWindow -> contentView (NSView, layer-backed) -> CAMetalLayer.
//
// This is the only file in WindowsBase that talks to the Objective-C runtime / AppKit. P/Invoke
// targets (libobjc, AppKit) are resolved lazily at call time so the file still compiles and ships
// on every platform; the frameworks only load when actually running on macOS.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>
    /// A single on-screen Cocoa window (NSWindow + layer-backed NSView). The NSView pointer is the
    /// cross-platform stand-in for an HWND: it is what <see cref="ContentView"/> returns and what
    /// the composition target / WebGPU surface are created against.
    /// </summary>
    // Public because the shared MS.Win32 native-method wrappers (GetWindowRect/SetWindowPos/...)
    // reference it, and those files are compiled into several WPF assemblies; the type itself is
    // defined once (in WindowsBase) so all callers share a single window map.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public sealed class CocoaWindow
    {
        private IntPtr _window;      // NSWindow*
        private IntPtr _contentView; // NSView* (layer-backed) - used as the window handle

        /// <summary>The NSView* that stands in for the HWND. IntPtr.Zero until <see cref="Create"/>.</summary>
        public IntPtr ContentView => _contentView;

        /// <summary>The NSWindow* backing this window.</summary>
        public IntPtr Window => _window;

        /// <summary>All live windows, keyed by their content-view handle, so the handle can be
        /// resolved back to its window (e.g. to query size) from code that only has the "hwnd".</summary>
        private static readonly Dictionary<IntPtr, CocoaWindow> s_byView = new();
        private static readonly object s_lock = new();

        public static CocoaWindow FromHandle(IntPtr view)
        {
            lock (s_lock)
            {
                return s_byView.TryGetValue(view, out CocoaWindow w) ? w : null;
            }
        }

        /// <summary>
        /// Create and show an NSWindow with a layer-backed content view. Must be called on the main
        /// (UI) thread. <paramref name="width"/>/<paramref name="height"/> are in points.
        /// </summary>
        public void Create(string title, int x, int y, int width, int height)
        {
            EnsureApplication();

            if (width <= 0) width = 800;
            if (height <= 0) height = 600;

            IntPtr nsWindowClass = objc_getClass("NSWindow");
            IntPtr alloc = Send(nsWindowClass, Sel("alloc"));

            var frame = new NSRect { x = x, y = y, width = width, height = height };
            const ulong styleMask =
                NSWindowStyleMaskTitled | NSWindowStyleMaskClosable |
                NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable;

            _window = SendInitWindow(alloc, Sel("initWithContentRect:styleMask:backing:defer:"),
                                     frame, styleMask, NSBackingStoreBuffered, false);

            // Make the content view layer-backed so wgpu can install a CAMetalLayer on it.
            _contentView = Send(_window, Sel("contentView"));
            SendVoidBool(_contentView, Sel("setWantsLayer:"), true);

            if (!string.IsNullOrEmpty(title))
            {
                IntPtr nsTitle = MakeNSString(title);
                SendVoidPtr(_window, Sel("setTitle:"), nsTitle);
            }

            SendVoidPtr(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            Send(_window, Sel("center"));

            lock (s_lock)
            {
                s_byView[_contentView] = this;
            }
        }

        /// <summary>Resize the window's content area to the given size in points.</summary>
        public void SetContentSize(int width, int height)
        {
            if (_window == IntPtr.Zero || width <= 0 || height <= 0) return;
            SendVoidSize(_window, Sel("setContentSize:"), new NSSize { width = width, height = height });
        }

        /// <summary>Current content-view size in points (device-independent units).</summary>
        public void GetContentSize(out int width, out int height)
        {
            width = 0; height = 0;
            if (_contentView == IntPtr.Zero) return;

            NSRect bounds = SendRect(_contentView, Sel("bounds"));
            width = (int)Math.Round(bounds.width);
            height = (int)Math.Round(bounds.height);
        }

        /// <summary>Current content-view size in pixels (points * backing scale).</summary>
        public void GetPixelSize(out int width, out int height)
        {
            width = 0; height = 0;
            if (_contentView == IntPtr.Zero) return;

            NSRect bounds = SendRect(_contentView, Sel("bounds"));
            double scale = _window != IntPtr.Zero ? SendDouble(_window, Sel("backingScaleFactor")) : 1.0;
            if (scale <= 0) scale = 1.0;

            width = (int)Math.Round(bounds.width * scale);
            height = (int)Math.Round(bounds.height * scale);
        }

        /// <summary>Close and release the window.</summary>
        public void Destroy()
        {
            if (_contentView != IntPtr.Zero)
            {
                lock (s_lock)
                {
                    s_byView.Remove(_contentView);
                }
            }

            if (_window != IntPtr.Zero)
            {
                Send(_window, Sel("close"));
                _window = IntPtr.Zero;
            }
            _contentView = IntPtr.Zero;
        }

        // ---- NSApplication ----------------------------------------------------------

        private static bool s_appInitialized;

        /// <summary>
        /// Bring up NSApplication once so windows can be shown and events dispatched. Sets a
        /// regular activation policy (so the app gets a Dock icon / can become active) and calls
        /// finishLaunching. The actual event loop is pumped incrementally by <see cref="PumpEvents"/>.
        /// </summary>
        internal static void EnsureApplication()
        {
            if (s_appInitialized) return;
            s_appInitialized = true;

            // NSApplication/NSWindow live in AppKit; make sure it (and Foundation) are loaded into
            // the process before we ask the Objective-C runtime for those classes.
            const int RTLD_NOW = 2;
            dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_NOW);
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);

            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            // NSApplicationActivationPolicyRegular = 0
            SendVoidNInt(app, Sel("setActivationPolicy:"), 0);
            Send(app, Sel("finishLaunching"));
            SendVoidBool(app, Sel("activateIgnoringOtherApps:"), true);
        }

        /// <summary>
        /// Run the Cocoa event loop for up to <paramref name="maxMilliseconds"/>, then drain any
        /// remaining queued events. Blocking on nextEventMatchingMask actually *runs* the run loop
        /// (CoreAnimation commits, the window-server handshake that makes the layer visible, input
        /// delivery) - draining alone does not, which leaves the CAMetalLayer occluded. Called from
        /// the Dispatcher's run loop each frame in place of a bare managed wait.
        /// </summary>
        internal static void PumpEvents(int maxMilliseconds)
        {
            if (!s_appInitialized) return;

            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            IntPtr mode = s_defaultRunLoopMode ??= MakeNSString("kCFRunLoopDefaultMode");

            // Block up to maxMilliseconds for the first event (runs the run loop meanwhile)...
            IntPtr until = SendPtrDouble(objc_getClass("NSDate"), Sel("dateWithTimeIntervalSinceNow:"),
                                         maxMilliseconds / 1000.0);
            IntPtr evt = SendNextEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                                       ulong.MaxValue, until, mode, true);

            // ...then dispatch it and drain the rest without blocking. NSEventMaskAny = ulong.MaxValue.
            IntPtr distantPast = Send(objc_getClass("NSDate"), Sel("distantPast"));
            while (evt != IntPtr.Zero)
            {
                SendVoidPtr(app, Sel("sendEvent:"), evt);
                evt = SendNextEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                                    ulong.MaxValue, distantPast, mode, true);
            }
        }

        private static IntPtr? s_defaultRunLoopMode;

        // ---- helpers ----------------------------------------------------------------

        private static IntPtr MakeNSString(string s)
        {
            IntPtr cls = objc_getClass("NSString");
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            IntPtr buf = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buf, utf8.Length);
                return SendPtrRet(cls, Sel("stringWithUTF8String:"), buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        // objc_msgSend is variadic in C; declare one typed alias per call shape we use.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendInitWindow(IntPtr receiver, IntPtr selector, NSRect rect, ulong styleMask, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendNextEvent(IntPtr receiver, IntPtr selector, ulong mask, IntPtr untilDate, IntPtr mode, [MarshalAs(UnmanagedType.I1)] bool dequeue);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidSize(IntPtr receiver, IntPtr selector, NSSize size);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double x;
            public double y;
            public double width;
            public double height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSSize
        {
            public double width;
            public double height;
        }

        private const ulong NSWindowStyleMaskTitled = 1 << 0;
        private const ulong NSWindowStyleMaskClosable = 1 << 1;
        private const ulong NSWindowStyleMaskMiniaturizable = 1 << 2;
        private const ulong NSWindowStyleMaskResizable = 1 << 3;
        private const ulong NSBackingStoreBuffered = 2;
    }
}
