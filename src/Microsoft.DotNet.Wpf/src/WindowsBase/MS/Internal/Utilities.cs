// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MS.Internal
{
    /// <summary>
    /// General utility class for macro-type functions.
    /// </summary>
    internal static class Utilities
    {
        // These gates all mean "is this a Windows new enough to have feature X", and each one guards a
        // Windows-only path (a dwmapi/uxtheme P/Invoke, or Shell COM). Environment.OSVersion.Version
        // reports the KERNEL version off-Windows -- Darwin 25.x on macOS, 6.x on Linux -- which sails
        // past every one of these comparisons and lets those paths run. JumpList.ApplyList was the one
        // that surfaced it: its downlevel "fail fast" branch was never taken, so a plain
        // `new JumpList().Apply()` reached the STA verify guarding Shell's ICustomDestinationList and
        // threw "This operation requires the thread's apartment state to be 'STA'" on the real UI
        // thread -- apartments being a Windows concept that GetApartmentState answers Unknown for here.
        //
        // Answering false off-Windows lets every call site take the downlevel path it already has.
        // This mirrors the identically-named PresentationFramework copy
        // (System/Windows/Standard/Utilities.cs), which was already pinned this way; this one was not,
        // and it is the copy MS.Internal-importing files such as JumpList.cs actually bind to.
        private static readonly Version _osVersion =
            OperatingSystem.IsWindows() ? Environment.OSVersion.Version : new Version(0, 0);

        internal static bool IsOSVistaOrNewer
        {
            get { return _osVersion >= new Version(6, 0); }
        }

        internal static bool IsOSWindows7OrNewer
        {
            get { return _osVersion >= new Version(6, 1); }
        }

        internal static bool IsOSWindows8OrNewer
        {
            get { return _osVersion >= new Version(6, 2); }
        }
        
        internal static bool IsCompositionEnabled
        {
            get
            {
                if (!IsOSVistaOrNewer)
                {
                    return false;
                }

                PInvoke.DwmIsCompositionEnabled(out BOOL isDesktopCompositionEnabled).ThrowOnFailure();
                return isDesktopCompositionEnabled;
            }
        }

        internal static void SafeDispose<T>(ref T disposable) where T : IDisposable
        {
            // Dispose can safely be called on an object multiple times.
            IDisposable t = disposable;
            disposable = default(T);
            t?.Dispose();
        }
        
        internal static void SafeRelease<T>(ref T comObject) where T : class
        {
            T t = comObject;
            comObject = default(T);
            if (null != t)
            {
                Debug.Assert(Marshal.IsComObject(t));
                Marshal.ReleaseComObject(t);
            }
        }
    }
}
