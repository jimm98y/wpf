// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

#if WINDOWS_BASE
namespace MS.Internal.WindowsBase.Interop
#elif PRESENTATION_CORE
namespace System.Windows.Interop
#elif PRESENTATIONFRAMEWORK
namespace MS.Internal.PresentationFramework.Interop
#elif REACHFRAMEWORK
namespace MS.Internal.ReachFramework.Interop
#elif UIAUTOMATIONTYPES
namespace MS.Internal.UIAutomationTypes.Interop
#else
namespace Microsoft.Internal.Interop
#endif
{
    /// <summary>
    /// DevDiv:1158540
    ///
    /// Which Windows version we are on. This used to P/Invoke the same checks out of the
    /// PresentationNative helper DLL; it is managed now, so the answer is available on every
    /// platform this port builds for and costs no native dependency on any of them. That matters
    /// beyond tidiness: the system .CompositeFont files gate their FontFamilyCollection entries on
    /// a minimum OS, so a version probe that throws takes the whole script-fallback chain with it
    /// and every non-Latin run renders as missing-glyph boxes.
    ///
    /// To add a new OS:
    ///     Make sure you have followed the instructions in OperatingSystemVersion.cs to get here
    ///     Add a probe for your new Is{OSName}OrGreater property to the static constructor
    ///     Add case to switch statement in IsOsVersionOrGreater
    ///     Add new if statement to the TOP of GetOsVersion
    /// </summary>
    internal static class OSVersionHelper
    {
        #region Static OS Members

        internal static bool IsOsWindows10RS5OrGreater { get; set; }

        internal static bool IsOsWindows10RS4OrGreater { get; set; }

        internal static bool IsOsWindows10RS3OrGreater { get; set; }

        internal static bool IsOsWindows10RS2OrGreater { get; set; }

        internal static bool IsOsWindows10RS1OrGreater { get; set; }

        internal static bool IsOsWindows10TH2OrGreater { get; set; }

        internal static bool IsOsWindows10TH1OrGreater { get; set; }

        internal static bool IsOsWindows10OrGreater { get; set; }

        internal static bool IsOsWindows8Point1OrGreater { get; set; }

        internal static bool IsOsWindows8OrGreater { get; set; }

        internal static bool IsOsWindows7SP1OrGreater { get; set; }

        internal static bool IsOsWindows7OrGreater { get; set; }

        internal static bool IsOsWindowsVistaSP2OrGreater { get; set; }

        internal static bool IsOsWindowsVistaSP1OrGreater { get; set; }

        internal static bool IsOsWindowsVistaOrGreater { get; set; }

        internal static bool IsOsWindowsXPSP3OrGreater { get; set; }

        internal static bool IsOsWindowsXPSP2OrGreater { get; set; }

        internal static bool IsOsWindowsXPSP1OrGreater { get; set; }

        internal static bool IsOsWindowsXPOrGreater { get; set; }

        internal static bool IsOsWindowsServer { get; set; }

        #endregion

        #region Constructor

        static OSVersionHelper()
        {
            // Off-Windows every "is this Windows version or greater" answer is false, so leave all
            // properties at their default.
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // The build numbers are the ones the native helper used (wpfsdkddkver.h). RS4 (17134)
            // never had a native counterpart in this tree at all -- the P/Invoke was declared but no
            // implementation was built -- which is one more reason the managed probe is the better
            // answer. OperatingSystem.IsWindowsVersionAtLeast reads the real version through
            // RtlGetVersion, so it is not subject to the app-manifest version lie that GetVersionEx
            // suffers from, exactly like RtlVerifyVersionInfo before it.
            IsOsWindows10RS5OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

            IsOsWindows10RS4OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);

            IsOsWindows10RS3OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

            IsOsWindows10RS2OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063);

            IsOsWindows10RS1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393);

            IsOsWindows10TH2OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10586);

            IsOsWindows10TH1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

            IsOsWindows10OrGreater = OperatingSystem.IsWindowsVersionAtLeast(10, 0);

            IsOsWindows8Point1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 3);

            IsOsWindows8OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 2);

            // The service-pack component of these older checks is dropped rather than emulated:
            // .NET does not surface a service-pack level (Environment.OSVersion.ServicePack is
            // always empty on .NET Core), and it cannot change any answer here. This runtime does
            // not load below Windows 10, so every one of these is true whenever we are on Windows
            // at all -- as the version ladder below, which stops at Windows 10, already assumes.
            IsOsWindows7SP1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 1);

            IsOsWindows7OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 1);

            IsOsWindowsVistaSP2OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 0);

            IsOsWindowsVistaSP1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 0);

            IsOsWindowsVistaOrGreater = OperatingSystem.IsWindowsVersionAtLeast(6, 0);

            IsOsWindowsXPSP3OrGreater = OperatingSystem.IsWindowsVersionAtLeast(5, 1);

            IsOsWindowsXPSP2OrGreater = OperatingSystem.IsWindowsVersionAtLeast(5, 1);

            IsOsWindowsXPSP1OrGreater = OperatingSystem.IsWindowsVersionAtLeast(5, 1);

            IsOsWindowsXPOrGreater = OperatingSystem.IsWindowsVersionAtLeast(5, 1);

            IsOsWindowsServer = IsWindowsServer();
        }

        /// <summary>
        /// True on a Server SKU. There is no managed API for the product type, so this asks ntdll
        /// directly -- a plain P/Invoke to a system DLL, which is what the native helper did too
        /// (RtlGetVersion fills in wProductType; anything other than VER_NT_WORKSTATION is Server).
        /// A failure is reported as "not Server" rather than thrown: no caller in the tree reads
        /// this, and it must not be the reason a text run fails to format.
        /// </summary>
        private static bool IsWindowsServer()
        {
            const byte VER_NT_WORKSTATION = 1;

            try
            {
                RTL_OSVERSIONINFOEXW osvi = default;
                osvi.dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOEXW>();

                // STATUS_SUCCESS
                if (RtlGetVersion(ref osvi) != 0)
                {
                    return false;
                }

                return osvi.wProductType != VER_NT_WORKSTATION;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("ntdll.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW versionInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOEXW
        {
            internal uint dwOSVersionInfoSize;
            internal uint dwMajorVersion;
            internal uint dwMinorVersion;
            internal uint dwBuildNumber;
            internal uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string szCSDVersion;
            internal ushort wServicePackMajor;
            internal ushort wServicePackMinor;
            internal ushort wSuiteMask;
            internal byte wProductType;
            internal byte wReserved;
        }

        #endregion

        #region Managed API

        internal static bool IsOsVersionOrGreater(OperatingSystemVersion osVer)
        {
            // Off-Windows the native Is*OrGreater probes are all false (see the static ctor), but
            // GetOsVersion() reports the newest known version (Windows10RS5). Keep this query consistent
            // with that: our reported OS is >= any queried version. Without this, OS-version-gated
            // resources -- notably the system .CompositeFont FontFamilyCollection entries, which are keyed
            // by minimum OS -- find no matching entry and throw (breaks Fonts.SystemFontFamilies).
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            switch (osVer)
            {
                case OperatingSystemVersion.Windows10RS5:
                    return IsOsWindows10RS5OrGreater;
                case OperatingSystemVersion.Windows10RS4:
                    return IsOsWindows10RS4OrGreater;
                case OperatingSystemVersion.Windows10RS3:
                    return IsOsWindows10RS3OrGreater;
                case OperatingSystemVersion.Windows10RS2:
                    return IsOsWindows10RS2OrGreater;
                case OperatingSystemVersion.Windows10RS1:
                    return IsOsWindows10RS1OrGreater;
                case OperatingSystemVersion.Windows10TH2:
                    return IsOsWindows10TH2OrGreater;
                case OperatingSystemVersion.Windows10:
                    return IsOsWindows10OrGreater;
                case OperatingSystemVersion.Windows8Point1:
                    return IsOsWindows8Point1OrGreater;
                case OperatingSystemVersion.Windows8:
                    return IsOsWindows8OrGreater;
                case OperatingSystemVersion.Windows7SP1:
                    return IsOsWindows7SP1OrGreater;
                case OperatingSystemVersion.Windows7:
                    return IsOsWindows7OrGreater;
                case OperatingSystemVersion.WindowsVistaSP2:
                    return IsOsWindowsVistaSP2OrGreater;
                case OperatingSystemVersion.WindowsVistaSP1:
                    return IsOsWindowsVistaSP1OrGreater;
                case OperatingSystemVersion.WindowsVista:
                    return IsOsWindowsVistaOrGreater;
                case OperatingSystemVersion.WindowsXPSP3:
                    return IsOsWindowsXPSP3OrGreater;
                case OperatingSystemVersion.WindowsXPSP2:
                    return IsOsWindowsXPSP2OrGreater;
            }

            throw new ArgumentException($"{osVer} is not a valid OS!", nameof(osVer));
        }

        internal static OperatingSystemVersion GetOsVersion()
        {
            if (IsOsWindows10RS5OrGreater)
            {
                return OperatingSystemVersion.Windows10RS5;
            }
            else if (IsOsWindows10RS4OrGreater)
            {
                return OperatingSystemVersion.Windows10RS4;
            }
            else if (IsOsWindows10RS3OrGreater)
            {
                return OperatingSystemVersion.Windows10RS3;
            }
            else if (IsOsWindows10RS2OrGreater)
            {
                return OperatingSystemVersion.Windows10RS2;
            }
            else if (IsOsWindows10RS1OrGreater)
            {
                return OperatingSystemVersion.Windows10RS1;
            }
            else if (IsOsWindows10TH2OrGreater)
            {
                return OperatingSystemVersion.Windows10TH2;
            }
            else if (IsOsWindows10OrGreater)
            {
                return OperatingSystemVersion.Windows10;
            }
            else if (IsOsWindows8Point1OrGreater)
            {
                return OperatingSystemVersion.Windows8Point1;
            }
            else if (IsOsWindows8OrGreater)
            {
                return OperatingSystemVersion.Windows8;
            }
            else if (IsOsWindows7SP1OrGreater)
            {
                return OperatingSystemVersion.Windows7SP1;
            }
            else if (IsOsWindows7OrGreater)
            {
                return OperatingSystemVersion.Windows7;
            }
            else if (IsOsWindowsVistaSP2OrGreater)
            {
                return OperatingSystemVersion.WindowsVistaSP2;
            }
            else if (IsOsWindowsVistaSP1OrGreater)
            {
                return OperatingSystemVersion.WindowsVistaSP1;
            }
            else if (IsOsWindowsVistaOrGreater)
            {
                return OperatingSystemVersion.WindowsVista;
            }
            else if (IsOsWindowsXPSP3OrGreater)
            {
                return OperatingSystemVersion.WindowsXPSP3;
            }
            else if (IsOsWindowsXPSP2OrGreater)
            {
                return OperatingSystemVersion.WindowsXPSP2;
            }

            // Off-Windows all the IsOsWindows* probes are false by design; report the
            // newest known version so version-gated features (composite font typographic
            // defaults etc.) take their modern paths instead of throwing.
            if (!OperatingSystem.IsWindows())
            {
                return OperatingSystemVersion.Windows10RS5;
            }

            throw new Exception("OSVersionHelper.GetOsVersion Could not detect OS!");
        }

        #endregion
    }
}
