// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//  File: SafeSystemMetrics.cs
//  This class is copied from the system metrics class in frameworks. The
//  reason it exists is to consolidate all system metric calls through one layer
//  so that maintenance from a security stand point gets easier. We will add
//  mertrics on a need basis. The caching code is removed since the original calls 
//  that were moved here do not rely on caching. If there is a percieved perf. problem
//  we can work on enabling this.

using MS.Internal.Interop;

namespace MS.Win32
{
    /// <summary>
    ///     Contains properties that are queries into the system's various settings.
    /// </summary>
    internal sealed class SafeSystemMetrics
    {

        private SafeSystemMetrics()
        {
        }

        // GetSystemMetrics is a user32 call that only exists on Windows. Off-Windows this layer
        // returns the same value Windows uses by default, so callers get sensible metrics until a
        // cross-platform windowing backend supplies real ones. Keeping the OS gate here (the single
        // consolidation point for system metrics) avoids scattering platform checks at every call.
        private static int GetSystemMetric(SM index, int nonWindowsDefault)
        {
            return OperatingSystem.IsWindows()
                ? UnsafeNativeMethods.GetSystemMetrics(index)
                : nonWindowsDefault;
        }

#if !PRESENTATION_CORE
        /// <summary>
        ///     Maps to SM_CXVIRTUALSCREEN
        /// </summary>
        internal static int VirtualScreenWidth
        {
            get
            {
                return GetSystemMetric(SM.CXVIRTUALSCREEN, 0);
            }
        }

        /// <summary>
        ///     Maps to SM_CYVIRTUALSCREEN
        /// </summary>
        internal static int VirtualScreenHeight
        {
            get
            {
                return GetSystemMetric(SM.CYVIRTUALSCREEN, 0);
            }
        }
#endif //end !PRESENTATIONCORE

        /// <summary>
        ///     Maps to SM_CXDOUBLECLK
        /// </summary>
        internal static int DoubleClickDeltaX
        {
            get
            {
                return GetSystemMetric(SM.CXDOUBLECLK, 4);
            }
        }

        /// <summary>
        ///     Maps to SM_CYDOUBLECLK
        /// </summary>
        internal static int DoubleClickDeltaY
        {
            get
            {
                return GetSystemMetric(SM.CYDOUBLECLK, 4);
            }
        }


        /// <summary>
        ///     Maps to SM_CXDRAG
        /// </summary>
        internal static int DragDeltaX
        {
            get
            {
                return GetSystemMetric(SM.CXDRAG, 4);
            }
        }

        /// <summary>
        ///     Maps to SM_CYDRAG
        /// </summary>
        internal static int DragDeltaY
        {
            get
            {
                return GetSystemMetric(SM.CYDRAG, 4);
            }
        }

        ///<summary>
        /// Is an IMM enabled ? Maps to SM_IMMENABLED
        ///</summary>
        internal static bool IsImmEnabled
        {
            get
            {
                // The Windows Input Method Manager does not exist off-Windows.
                return OperatingSystem.IsWindows() && (UnsafeNativeMethods.GetSystemMetrics(SM.IMMENABLED) != 0);
            }

        }

    }
}
