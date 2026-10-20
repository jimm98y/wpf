// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WPF window owned by a WinForms window. On this stack a WinForms Control.Handle is the
// XplatUIWebGpu driver's managed handle, not an OS window: the form is presented in a native host
// window of its own (Win32Host on Windows). A window handed such a handle as its owner --
// WindowInteropHelper.Owner = form.Handle, or a third-party dialog that takes the active window from
// its own user32 calls (which the stack answers with driver handles) -- passed it to CreateWindowEx
// as hWndParent, and the window was never created: "Invalid window handle". Telerik's file dialogs
// over GraphWorX failed so.
//
// The owner is the form's native host window instead. WinForms publishes the mapping in AppContext
// ("WinFormsWebGpu.HostWindowOf", from EmbeddedScenes.PublishHostWindow), so WPF needs no reference
// to the WinForms backend, as with HwndHostForeignContent.
//

using System;

namespace System.Windows.Interop
{
    internal static class ForeignOwnerWindow
    {
        internal const string AppContextKey = "WinFormsWebGpu.HostWindowOf";

        /// <summary>
        /// <paramref name="owner"/>, or the native window it is presented in when it is not an OS window
        /// but a WinForms driver handle whose form has one. Only Windows has OS owner windows to resolve to.
        /// </summary>
        internal static IntPtr Resolve(IntPtr owner)
        {
            if (owner == IntPtr.Zero || !OperatingSystem.IsWindows() || MS.Win32.UnsafeNativeMethods.IsWindow(new System.Runtime.InteropServices.HandleRef(null, owner)))
            {
                return owner;
            }

            if (AppContext.GetData(AppContextKey) is Func<IntPtr, IntPtr> hostWindowOf)
            {
                try
                {
                    IntPtr host = hostWindowOf(owner);
                    if (host != IntPtr.Zero)
                    {
                        return host;
                    }
                }
                catch
                {
                    // a failing resolver leaves the owner as it was given
                }
            }

            return owner;
        }
    }
}
