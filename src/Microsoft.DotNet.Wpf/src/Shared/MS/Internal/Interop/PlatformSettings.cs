// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The user's input settings as the system keeps them, for what Windows answers from its own (GetDoubleClickTime):
//
//   Windows  GetDoubleClickTime
//   macOS    the global com.apple.mouse.doubleClickThreshold (seconds; System Settings > Mouse's double-click speed),
//            half a second where it was never set, as NSEvent.doubleClickInterval answers
//   Linux    the desktop's, through xdg-desktop-portal: GNOME's org.gnome.desktop.peripherals.mouse double-click,
//            KDE's DoubleClickInterval; gsettings where there is no portal
//   Android  ViewConfiguration's double-tap timeout (300 ms)
//   else     Windows' default, 500 ms
//
// Read again every few seconds, so a change in the settings is seen by the next double-click.

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    public static class PlatformSettings
    {
        private const int WindowsDefault = 500, AndroidDoubleTapTimeout = 300;
        private static int s_doubleClick = -1;
        private static long s_readAt;

        /// <summary>The double-click time in milliseconds.</summary>
        public static int DoubleClickTime
        {
            get
            {
                long now = Environment.TickCount64;
                if (s_doubleClick < 0 || now - s_readAt > 5000)
                {
                    s_doubleClick = Read();
                    s_readAt = now;
                }
                return s_doubleClick;
            }
        }

        private static int Read()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return GetDoubleClickTime();
                if (OperatingSystem.IsAndroid()) return AndroidDoubleTapTimeout;
                if (OperatingSystem.IsMacOS()) return MacThreshold() is double s && s > 0 ? (int)Math.Round(s * 1000) : WindowsDefault;
                if (OperatingSystem.IsLinux()) return Wayland.LinuxDesktopSettings.DoubleClickTime() ?? WindowsDefault;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            return WindowsDefault;
        }

        [DllImport("user32.dll")]
        private static extern int GetDoubleClickTime();

        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
        [DllImport(CoreFoundation)] private static extern IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr application);
        [DllImport(CoreFoundation)] private static extern IntPtr CFGetTypeID(IntPtr value);
        [DllImport(CoreFoundation)] private static extern IntPtr CFNumberGetTypeID();
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out double value);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

        private static double? MacThreshold()
        {
            const uint Utf8 = 0x08000100;
            IntPtr key = CFStringCreateWithCString(IntPtr.Zero, "com.apple.mouse.doubleClickThreshold", Utf8);
            IntPtr any = CFStringCreateWithCString(IntPtr.Zero, ".GlobalPreferences", Utf8);    // kCFPreferencesAnyApplication
            try
            {
                IntPtr value = CFPreferencesCopyAppValue(key, any);
                if (value == IntPtr.Zero) return null;
                try { return CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, 13, out double s) ? s : null; }   // kCFNumberDoubleType
                finally { CFRelease(value); }
            }
            finally
            {
                CFRelease(key);
                CFRelease(any);
            }
        }
    }
}
