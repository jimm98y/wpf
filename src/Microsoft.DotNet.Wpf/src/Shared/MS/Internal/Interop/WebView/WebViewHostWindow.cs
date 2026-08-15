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

            return IntPtr.Zero;
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
        }

        internal static void Destroy(IntPtr window)
        {
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
