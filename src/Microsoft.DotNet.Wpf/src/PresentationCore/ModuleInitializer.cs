// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    [ModuleInitializer]
    public static void Initialize()
    {
        IsProcessDpiAware();

        // Native text backend (DirectWrite) load + teardown. All platform decisions live in
        // NativePlatform; on non-Windows this is a no-op until a text backend is ported.
        NativePlatform.InitializeTextBackend();

        MS.Internal.NativeWPFDLLLoader.LoadDwrite();
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
