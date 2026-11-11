// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The accent colour the user chose in the OS's own settings, for the heads where WinRT's UISettings
// (Windows' source of SystemColors.AccentColor and the Fluent theme's accent brushes) does not exist:
//
//   macOS  -- NSColor.controlAccentColor (System Settings > Appearance > Accent colour), in sRGB.
//   Linux  -- xdg-desktop-portal's org.freedesktop.appearance accent-color (GNOME 47+, KDE).
//
// Elsewhere -- iOS and Android have no system-wide accent a desktop app would follow, and the
// browser exposes none to script -- there is no answer, and WPF keeps Windows' default blue.
//

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    internal static unsafe class PlatformAccentColor
    {
        /// <summary>The user's accent colour as opaque sRGB; false where the OS has none to give.</summary>
        internal static bool TryGet(out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            try
            {
                if (OperatingSystem.IsMacOS()) return TryGetMac(out r, out g, out b);
                if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                    return Wayland.LinuxDesktopSettings.TryGetAccentColor(out r, out g, out b);
            }
            catch
            {
                // a colour we cannot read costs the colour, never the app
            }
            return false;
        }

        private static bool TryGetMac(out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            IntPtr accent = Send(objc_getClass("NSColor"), sel_registerName("controlAccentColor"));
            IntPtr srgb = Send(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
            if (accent == IntPtr.Zero || srgb == IntPtr.Zero) return false;
            // controlAccentColor is a catalog colour: its components exist only once it is in a colour space
            IntPtr color = SendPtr(accent, sel_registerName("colorUsingColorSpace:"), srgb);
            if (color == IntPtr.Zero) return false;
            double red, green, blue, alpha;
            SendGetRgba(color, sel_registerName("getRed:green:blue:alpha:"), &red, &green, &blue, &alpha);
            static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            (r, g, b) = (Channel(red), Channel(green), Channel(blue));
            return true;
        }

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendGetRgba(IntPtr receiver, IntPtr selector, double* r, double* g, double* b, double* a);
    }
}
