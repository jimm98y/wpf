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
// WindowsFormsHost sidesteps that by deriving from FrameworkElement instead and bridging at the
// scene level. Applications that subclass HwndHost DIRECTLY cannot: SharpDevelop's
// CustomWindowsFormsHost, for one, derives from HwndHost to get its own keyboard routing, and hands
// back a WinForms Control's Handle.
//
// So HwndHost asks here first. If something claims the handle, the host runs in FOREIGN mode: the
// Win32 paths are skipped and the claimant owns presentation, hit-testing and lifetime. Nothing in
// PresentationFramework knows how that is done - the delegates are filled in by
// WindowsFormsIntegration, which is the assembly that can see both WPF and the driver. That keeps
// the dependency direction the rest of the fork maintains: WPF never references the backend.
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
