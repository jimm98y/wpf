// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Selects the platform IWebViewBackend, mirroring the fork's IMediaBackend / IPlatformWindow /
// NativePlatform OS dispatch.
//
// This is a file of its own, not a companion class in IWebViewBackend.cs, because it is the ONLY
// thing in the seam that names the concrete backends. Keeping it separate means the interface, the
// vocabulary types and the scripting helpers can be compiled -- and therefore tested -- without
// dragging in six platform implementations, which is exactly what Wpf.WebView.Tests does.
//

using System;

namespace MS.Internal.Interop.WebView
{
    internal static class WebViewBackendFactory
    {
        /// <summary>
        /// The backend for this OS, or null where none exists.
        /// </summary>
        /// <remarks>
        /// The probe ORDER matters for the same reasons it does in MediaBackendFactory and
        /// NativePlatform: iOS and macOS are both Darwin, and Android is Linux as far as
        /// OperatingSystem is concerned, so the more specific head has to be asked first or it is
        /// answered by its sibling's backend.
        /// </remarks>
        internal static IWebViewBackend Create()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsWebViewBackend();
            }

            // Safe ahead of the iOS test, and deliberately matching MediaBackendFactory: this is
            // OperatingSystem.IsMacOS, which answers FALSE on iOS. NativePlatform has to order the
            // two the other way round only because it probes with
            // RuntimeInformation.IsOSPlatform(OSPlatform.OSX), which answers true on both.
            if (OperatingSystem.IsMacOS())
            {
                return new MacWebViewBackend();
            }

            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
            {
                return new IOSWebViewBackend();
            }

            if (OperatingSystem.IsBrowser())
            {
                return new BrowserWebViewBackend();
            }

            // Android BEFORE Linux, and that is not cosmetic: Android IS Linux as far as
            // OperatingSystem is concerned, so asking the other way round answers every Android
            // device with the Linux backend.
            if (OperatingSystem.IsAndroid())
            {
                return new AndroidWebViewBackend();
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxWebViewBackend();
            }

            // No engine on this platform. The caller must surface that rather than show an empty
            // rectangle -- see the class remarks on IWebViewBackend.
            return null;
        }
    }
}
