// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The single place PresentationCore's startup path decides platform-specific native
// behavior. Historically these were raw Win32 P/Invokes inlined at the call site
// (e.g. SetProcessDPIAware in ModuleInitializer, dwrite.dll loading in DWriteLoader),
// which meant porting to a new OS required hunting Win32 calls across the codebase.
//
// Following the WebGPU engine's Composition/Platform pattern (NativePlatform dispatch ->
// Win32Interop / MacInterop / LinuxInterop backends), every OS-gated native decision made
// during module init is funnelled through NativePlatform, and the Windows P/Invokes live in
// Win32Interop. Adding a macOS/Linux implementation means filling in one backend here rather
// than scattering `OperatingSystem.IsWindows()` checks through the startup code.
//

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MS.Internal.Text.TextInterface;

namespace MS.Internal.Interop
{
    /// <summary>
    /// Cross-platform dispatch for the native operations PresentationCore performs at load time.
    /// Windows routes to <see cref="Win32Interop"/>; other platforms currently no-op until their
    /// backend is ported (DPI/scale and the text stack are owned by the per-OS windowing layer).
    /// </summary>
    internal static class NativePlatform
    {
        /// <summary>
        /// Opt the process into DPI awareness. On Windows this is the user32 SetProcessDPIAware
        /// call; on other platforms the windowing backend owns DPI/scale, so there is nothing to do.
        /// </summary>
        internal static void SetProcessDpiAwareness()
        {
            if (OperatingSystem.IsWindows())
            {
                Win32Interop.SetProcessDpiAware();
            }
        }

        /// <summary>
        /// Load the native text/shaping backend and register its process-exit teardown. On Windows
        /// this loads DirectWrite (via <see cref="DWriteLoader"/>); on other platforms it is a no-op
        /// until a cross-platform text backend (e.g. CoreText/HarfBuzz) is ported, at which point
        /// its initialization belongs here.
        /// </summary>
        internal static void InitializeTextBackend()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            DWriteLoader.LoadDWrite();

            AppDomain.CurrentDomain.ProcessExit += static (object sender, EventArgs e) =>
            {
                DWriteLoader.UnloadDWrite();
            };
        }
    }

    /// <summary>
    /// The one place PresentationCore's startup path calls user32/kernel32 directly. Guarded with
    /// <see cref="SupportedOSPlatformAttribute"/> so the platform analyzer flags any call that is
    /// not first gated by <see cref="NativePlatform"/> on Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Win32Interop
    {
        internal static void SetProcessDpiAware() => SetProcessDPIAware_Internal();

        [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")]
        private static extern void SetProcessDPIAware_Internal();
    }
}
