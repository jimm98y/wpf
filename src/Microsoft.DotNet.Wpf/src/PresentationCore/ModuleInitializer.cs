// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MS.Internal.Interop;
using MS.Internal.Text.TextInterface;

internal static class ModuleInitializer
{
    /// <summary>
    /// DirectWriteForwarder has a module constructor that implements
    /// the setting of the default DPI awareness for the process.
    /// We need to load DirectWriteForwarder the instant PresentationCore
    /// loads in order to ensure that this is set before any DPI sensitive
    /// operations are carried out.  To do this, we simply call LoadDwrite
    /// as the module constructor for DirectWriteForwarder would do this anyway.
    /// </summary>
#pragma warning disable CA2255
    // NOTE: intentionally NOT a [ModuleInitializer]. mono-aot-cross does not AOT-compile a <Module>.cctor,
    // and the wasm interpreter cannot run one in AOT mode ("NIY encountered in method <Module>:.cctor" ->
    // fatal g_assert). This init is a no-op off-Windows anyway (DPI awareness + native DirectWrite load),
    // so on wasm/mac nothing is lost. On Windows it must be invoked from an early startup path instead
    // (before the first window) to preserve process DPI awareness.
    private static bool s_initialized;
    private static bool s_windowingInitialized;

    public static void Initialize()
    {
        // Let a platform accessibility backend ask for the automation tree. The backends live in
        // WindowsBase and cannot reference this assembly, so the request arrives as an event and is
        // answered here. Subscribing (not activating) costs nothing: no tree is built until an
        // assistive technology is actually detected.
        MS.Internal.Interop.AutomationTree.ActivationRequested += MS.Internal.Automation.AutomationBridge.Activate;

        // Linux: bring the Wayland connection up HERE, before anything else.
        //
        // This is an ordering requirement, not a convenience. The compositor connection is owned by
        // the windowing backend, and the WebGPU engine has to be handed that same wl_display when it
        // creates its instance -- the GLES backend cannot discover it (there is no
        // wl_proxy_get_display) and without it eglGetPlatformDisplay finds no windowing system and
        // silently falls back to a SURFACELESS EGL platform. Every later wgpuSurfaceConfigure then
        // fails with "Surface does not support the adapter's queue family", from inside Rust, as a
        // process abort.
        //
        // The engine initializes on the first render, which is AFTER the first window exists but can
        // be before that window's backend has connected -- so installing the seam from window
        // creation is too late. This runs from HwndSource's static constructor, which precedes both.
        if (System.OperatingSystem.IsLinux() && !System.OperatingSystem.IsAndroid())
        {
            if (!s_windowingInitialized)
            {
                s_windowingInitialized = true;
                try
                {
                    MS.Internal.Interop.Wayland.WaylandWindow.EnsureApplication();
                }
                catch (Exception)
                {
                    // No compositor (a headless or console process that merely loads PresentationCore).
                    // Not fatal here; window creation reports it properly, with the diagnostics.
                }
            }
            return;
        }

        if (!System.OperatingSystem.IsWindows())
            return;

        // Idempotent: this is invoked from an early startup path (HwndSource's static ctor, before
        // the first HWND) rather than a <Module>.cctor, so guard against a second invocation loading
        // DirectWrite twice or re-poking DPI awareness.
        if (s_initialized)
            return;
        s_initialized = true;

        IsProcessDpiAware();

#if !WPF_DWRITE_MANAGED_STUB
        // Native text backend (DirectWrite) load + teardown. All platform decisions live in
        // NativePlatform; on non-Windows this is a no-op until a text backend is ported.
        // Skipped entirely for the managed OpenType backend (the WebGPU port): there is no native
        // DirectWrite to load, and pulling wpfgfx/dwrite in from HwndSource's static ctor blocks the
        // type-initializer (spinning cursor / frozen window).
        NativePlatform.InitializeTextBackend();

        MS.Internal.NativeWPFDLLLoader.LoadDwrite();
#endif
    }
#pragma warning restore CA2255

    private static void IsProcessDpiAware()
    {
        bool disableDpiAware = false;

        // By default, Application is DPIAware.
        Assembly assemblyApp = Assembly.GetEntryAssembly();

        // Check if the Application has explicitly set DisableDpiAwareness attribute.
        if (assemblyApp != null && Attribute.IsDefined(assemblyApp, typeof(System.Windows.Media.DisableDpiAwarenessAttribute)))
        {
            disableDpiAware = true;
        }

        if (!disableDpiAware)
        {
            // DpiAware composition is enabled for this application. The actual native call is a
            // Win32/user32 concept; NativePlatform routes it to the Win32 backend on Windows and
            // no-ops elsewhere (the windowing backend owns DPI/scale on other platforms).
            NativePlatform.SetProcessDpiAwareness();
        }

        // Only when DisableDpiAwareness attribute is set in Application assembly,
        // It will ignore the SetProcessDPIAware API call.
    }
}
