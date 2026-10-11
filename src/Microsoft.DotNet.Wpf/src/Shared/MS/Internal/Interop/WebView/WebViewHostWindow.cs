// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The bare child window a web view's overlay lives in.
//
// The engine's native view is not parented to the WPF window directly. It gets a plain, empty child
// window of its own, and that window is what WPF positions. The indirection is what buys the control
// everything HwndHost already knows how to do -- clipping to ancestors, moving with layout, the DPI
// transition check, destruction order -- without any of it having to be re-implemented per engine.
// The engine then simply fills its window edge to edge (SetBounds(0, 0, w, h)), so no engine ever
// needs to know where the control sits on screen.
//
// It also keeps HwndHost's contract satisfiable: BuildWindowCore must hand back a real WS_CHILD
// window parented to the window it was given, and HwndHost verifies all three of those things
// (IsWindow, the WS_CHILD style bit, and GetParent) before it will use it.
//
// Only the Windows implementation exists so far. The other heads have no HWNDs at all: off Windows a
// "child window" is a borderless native window minted by HwndWrapper, and the overlay is a real
// subview/subsurface (NSView, UIView, wl_subsurface, a FrameLayout child, an <iframe>). Those arrive
// with their IPlatformWindow overlay members; until then Create answers IntPtr.Zero and the CONTROL
// reports that this head cannot host web content.
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.WebView
{
    internal static class WebViewHostWindow
    {
        /// <summary>
        /// Create the empty child window the engine will fill, or IntPtr.Zero where this head has no
        /// overlay host yet.
        /// </summary>
        internal static IntPtr Create(IntPtr parent, int width, int height)
        {
            if (OperatingSystem.IsWindows())
            {
                return Win32.Create(parent, width, height);
            }

            if (OperatingSystem.IsBrowser())
            {
                // No window to make: the browser head's overlay is an <iframe> the backend creates
                // in the DOM, keyed by this handle. All that is needed here is a value that is
                // unique and not zero -- the range avoids HwndWrapper's synthetic handles
                // (0x7F00_0000+) and BrowserWindow's (0x0B00_0000+), so a handle can always be told
                // apart from a window's by inspection.
                IntPtr handle = (IntPtr)NextBrowserHandle();
                // Remembered so the backend can place its iframe relative to the window's canvas:
                // the page is not the window, and a window need not sit at the page's origin.
                lock (s_browserParents) s_browserParents[handle] = parent;
                return handle;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// The next browser overlay key, unique in the PROCESS. This file is link-compiled into
        /// PresentationFramework, Microsoft.Web.WebView2.Core and System.Windows.Forms, and a static
        /// counter here is one counter per copy: a WebBrowser and a WebView2 in the same page both
        /// minted 0x0C000001, the JS side keeps one iframe per key, and the two controls ended up
        /// driving the same frame (each one's navigations reported by the other). The counter
        /// therefore lives in AppContext data, which every copy shares -- the same device the
        /// Android registration uses.
        /// </summary>
        private static int NextBrowserHandle()
        {
            const string Key = "MS.Internal.Interop.WebView.Browser.NextHandle";
            if (AppContext.GetData(Key) is not System.Runtime.CompilerServices.StrongBox<int> counter)
            {
                // The browser runs managed code on one thread, so there is no race to lose here.
                counter = new System.Runtime.CompilerServices.StrongBox<int>(0x0C00_0000);
                AppContext.SetData(Key, counter);
            }
            return System.Threading.Interlocked.Increment(ref counter.Value);
        }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> s_browserParents = new();

        /// <summary>The WPF window a browser overlay key was created in, or zero.</summary>
        internal static IntPtr BrowserParentOf(IntPtr window)
        {
            lock (s_browserParents)
                return s_browserParents.TryGetValue(window, out IntPtr parent) ? parent : IntPtr.Zero;
        }

        /// <summary>
        /// Move and resize the host window, in device pixels relative to the window it was created
        /// in.
        /// </summary>
        /// <remarks>
        /// WPF does not need this -- HwndHost already positions the window it was handed -- but
        /// WinForms does: on this stack a control's own handle is a managed counter, so nothing
        /// moves the engine's window unless the control asks. Keeping it here rather than in each
        /// control means one platform call to add per head instead of two.
        /// </remarks>
        internal static void Move(IntPtr window, int x, int y, int width, int height)
        {
            if (window != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                Win32.Move(window, x, y, Math.Max(1, width), Math.Max(1, height));
            }

            if (window != IntPtr.Zero && OperatingSystem.IsBrowser())
            {
                // There is no window to move: the iframe is the engine's whole view. Remember where
                // the "window" would be, and the backend adds it to the engine bounds it is given
                // (which a WinForms control gives as 0,0 -- filling its host window, as on Windows).
                // Without this the frame of a WinForms WebBrowser sat at the canvas's top-left
                // corner whatever the control's position.
                lock (s_browserParents) s_browserOffsets[window] = (x, y);
            }
        }

        private static readonly System.Collections.Generic.Dictionary<IntPtr, (int X, int Y)> s_browserOffsets = new();

        /// <summary>Where <see cref="Move"/> last put a browser overlay key's "window", in device
        /// pixels within the WPF window (or canvas) it was created in; zero when never moved.</summary>
        internal static (int X, int Y) BrowserOffsetOf(IntPtr window)
        {
            lock (s_browserParents)
                return s_browserOffsets.TryGetValue(window, out (int, int) offset) ? offset : (0, 0);
        }

        /// <summary>
        /// Destroy the host window. A no-op on the browser head: there is no window, and the
        /// backend's Detach removes the iframe -- it owns the DOM element, this only owns the key.
        /// </summary>
        internal static void Destroy(IntPtr window)
        {
            if (window != IntPtr.Zero && OperatingSystem.IsBrowser())
            {
                lock (s_browserParents)
                {
                    s_browserParents.Remove(window);
                    s_browserOffsets.Remove(window);
                }
            }

            if (window != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                Win32.Destroy(window);
            }
        }

        [SupportedOSPlatform("windows")]
        private static class Win32
        {
            private const int WS_CHILD = 0x40000000;
            private const int WS_VISIBLE = 0x10000000;
            private const int WS_CLIPCHILDREN = 0x02000000;
            private const int WS_CLIPSIBLINGS = 0x04000000;

            private const string ClassName = "WpfWebViewHost";

            private static bool s_registered;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WNDCLASSEX
            {
                public int cbSize;
                public int style;
                public IntPtr lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public IntPtr lpszMenuName;
                public IntPtr lpszClassName;
                public IntPtr hIconSm;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowExW(
                int exStyle, string className, string windowName, int style,
                int x, int y, int width, int height,
                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr GetModuleHandleW(IntPtr name);

            // Rooted for the lifetime of the process: the window class holds a raw pointer to this
            // delegate, and a collected delegate would be called on the next message.
            private static readonly WndProcDelegate s_wndProc = DefaultWndProc;

            private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

            private static IntPtr DefaultWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
                => DefWindowProcW(hwnd, msg, wParam, lParam);

            internal static IntPtr Create(IntPtr parent, int width, int height)
            {
                EnsureClass();

                // WS_CLIPCHILDREN/WS_CLIPSIBLINGS: the engine puts its own child windows in here, and
                // without them this window paints over them and they flicker on every move.
                return CreateWindowExW(
                    0, ClassName, string.Empty,
                    WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                    0, 0, Math.Max(1, width), Math.Max(1, height),
                    parent, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
            }

            internal static void Move(IntPtr window, int x, int y, int width, int height) =>
                SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

            internal static void Destroy(IntPtr window) => DestroyWindow(window);

            private const uint SWP_NOZORDER = 0x0004;
            private const uint SWP_NOACTIVATE = 0x0010;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                    int x, int y, int cx, int cy, uint flags);

            private static void EnsureClass()
            {
                if (s_registered)
                {
                    return;
                }

                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                    hInstance = GetModuleHandleW(IntPtr.Zero),
                    lpszClassName = Marshal.StringToHGlobalUni(ClassName),
                };

                // A zero return can also mean "already registered by an earlier AppDomain or a
                // previous load of this assembly", which is not an error -- CreateWindowEx will
                // answer for whether the class is actually usable.
                RegisterClassExW(ref wc);
                s_registered = true;
            }
        }
    }
}
