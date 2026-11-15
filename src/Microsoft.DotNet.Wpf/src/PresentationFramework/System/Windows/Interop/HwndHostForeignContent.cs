// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The seam that lets HwndHost host content whose "handle" is not an HWND.
//
// HwndHost's contract is Win32: BuildWindowCore returns a child window handle, and everything after
// that - the WS_CHILD check, SetParent, SetWindowPos, ShowWindow, the subclass - is a Win32 call on
// it. On this stack there are no HWNDs to hand it. The XplatUIWebGpu driver mints managed Hwnd
// objects and every window is composited through WebGPU, so IsWindow(handle) is false and BuildWindow
// throws ChildWindowNotCreated before the host has a chance to do anything.
//
// So HwndHost asks here first: WindowsFormsHost, whose BuildWindowCore returns its container form's
// handle, and applications that subclass HwndHost themselves -- SharpDevelop's
// CustomWindowsFormsHost, for one, derives from HwndHost to get its own keyboard routing, and hands
// back a WinForms Control's Handle.
//
// If something claims the handle, the host runs in FOREIGN mode: the Win32 paths are skipped and the
// claimant owns presentation, hit-testing and lifetime, and delivers the child's messages to the
// host's WndProc and MessageHook (DispatchMessage). Nothing in PresentationFramework knows how that is
// done - the delegates are filled in by WindowsFormsIntegration, which is the assembly that can see
// both WPF and the driver. That keeps the dependency direction the rest of the fork maintains: WPF
// never references the backend.
//

using System;

namespace System.Windows.Interop
{
    /// <summary>
    /// Lets a component that understands non-HWND child content claim the handles
    /// <see cref="HwndHost.BuildWindowCore"/> returns.
    /// </summary>
    public static class HwndHostForeignContent
    {
        /// <summary>
        /// Offered every handle that is not a real window. Return true to claim it, after which the
        /// claimant is responsible for presenting the content and <see cref="HwndHost"/> performs no
        /// Win32 operations on it.
        /// </summary>
        public static Func<HwndHost, IntPtr, bool> Attach { get; set; }

        /// <summary>Called when a claimed host is torn down.</summary>
        public static Action<HwndHost, IntPtr> Detach { get; set; }

        /// <summary>
        /// Called when the host's layout changes, in device pixels relative to the render root.
        /// </summary>
        public static Action<HwndHost, IntPtr, int, int, int, int> SetBounds { get; set; }

        /// <summary>Called when the host's visibility changes.</summary>
        public static Action<HwndHost, IntPtr, bool> SetVisible { get; set; }

        /// <summary>
        /// Delivers a message of a claimed child to the host -- its <c>WndProc</c> override, then its
        /// <c>MessageHook</c> handlers -- as subclassing a child window would on Win32. A claimant calls
        /// this before handling the message itself, and leaves it alone when <paramref name="handled"/>
        /// comes back true. Messages for any other handle are ignored.
        /// </summary>
        public static IntPtr DispatchMessage(HwndHost host, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            ArgumentNullException.ThrowIfNull(host);
            return host.ForeignWndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        internal static bool TryAttach(HwndHost host, IntPtr handle)
        {
            Func<HwndHost, IntPtr, bool> attach = Attach;
            if (attach is null || handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return attach(host, handle);
            }
            catch
            {
                // A failing adapter must not turn into a different exception out of BuildWindow;
                // declining the handle produces the original, accurate ChildWindowNotCreated.
                return false;
            }
        }
    }
}
