// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows.Interop;
using System.Windows.Media;
using MS.Internal;
using Standard;
using HRESULT = Standard.HRESULT;

// ReSharper disable once CheckNamespace
namespace System.Windows.Appearance;

internal static class WindowBackdropManager
{
    internal static bool IsSupported(WindowBackdropType backdropType)
    {
        // Off-Windows the DWM backdrop types are emulated by the native window layer (an
        // NSVisualEffectView behind-window blur on macOS -- the Mica substitute for the WebGPU
        // compositor). None is always supported; the material types are supported where we have
        // a native backdrop implementation.
        if (!OperatingSystem.IsWindows())
        {
            return backdropType == WindowBackdropType.None || OperatingSystem.IsMacOS();
        }

        return backdropType switch
        {
            WindowBackdropType.Auto => Utility.IsWindows11_22H2OrNewer,
            WindowBackdropType.TabbedWindow => Utility.IsWindows11_22H2OrNewer,
            WindowBackdropType.MainWindow => Utility.IsOSWindows11OrNewer,
            WindowBackdropType.TransientWindow => Utility.IsOSWindows7OrNewer,
            WindowBackdropType.None => true,
            _ => false
        };
    }

    internal static bool SetBackdrop(Window window, WindowBackdropType backdropType)
    {
        if (window is null ||
                !IsSupported(backdropType) ||
                window.AllowsTransparency ||
!IsBackdropEnabled)
        {
            return false;
        }

        if(!ThemeManager.IsFluentThemeEnabled && window.ThemeMode == ThemeMode.None)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        bool result = SetBackdropCore(handle, backdropType);

        // Off-Windows the native Mica material (NSVisualEffectView) is kept in DARK appearance so it always
        // shows the wallpaper. Tint the window's own background with the theme's translucent LAYER fill
        // (LayerFillColorDefault: ~50% white in light, ~30% dark in dark) rather than leaving it fully
        // transparent -- otherwise fully-transparent window regions (nav pane, title bar, gaps) show the raw
        // dark backdrop and light-theme black text on them is illegible. The semi-transparent fill still
        // lets the wallpaper show through while giving those regions a legible, theme-appropriate tint (the
        // off-Windows stand-in for the light/dark tint DWM Mica applies). Tracks the theme via a resource
        // reference; cleared when the backdrop is removed.
        if (result && OperatingSystem.IsMacOS())
        {
            if (backdropType == WindowBackdropType.None)
            {
                window.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            }
            else
            {
                window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "LayerFillColorDefaultBrush");
            }
        }

        return result;
    }

    #region Private Methods

    private static bool SetBackdropCore(IntPtr hwnd, WindowBackdropType backdropType)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // Off-Windows there is no DWM; route to the native window's translucent backdrop material
        // (NSVisualEffectView on macOS) instead of the DwmSetWindowAttribute path below.
        if (!OperatingSystem.IsWindows())
        {
            return SetBackdropCoreNonWindows(hwnd, backdropType);
        }

        if (backdropType == WindowBackdropType.None)
        {
            RestoreBackground(hwnd);
            return RemoveBackdrop(hwnd);
        }

        RemoveBackground(hwnd);
        return ApplyBackdrop(hwnd, backdropType);
    }

    // macOS/WebGPU Mica substitute: the swap-chain content must composite over the native backdrop,
    // so clear the composition background to transparent (as RemoveBackground does for DWM) and turn
    // on the window's translucent material behind the transparent content. Where WPF paints the
    // (Fluent-Transparent) window background the blur shows through, mirroring Windows 11 Mica.
    private static bool SetBackdropCoreNonWindows(IntPtr hwnd, WindowBackdropType backdropType)
    {
        if (backdropType == WindowBackdropType.None)
        {
            RestoreBackground(hwnd);
            if (OperatingSystem.IsMacOS())
            {
                MS.Internal.Interop.CocoaWindow.FromHandle(hwnd)?.DisableMicaBackdrop();
            }
            return true;
        }

        RemoveBackground(hwnd);
        if (OperatingSystem.IsMacOS())
        {
            MS.Internal.Interop.CocoaWindow.FromHandle(hwnd)?.EnableMicaBackdrop();
            return true;
        }
        return false;
    }

    private static bool ApplyBackdrop(IntPtr hwnd, WindowBackdropType backdropType)
    {
        UpdateGlassFrame(hwnd, backdropType);

        var backdropPvAttribute = backdropType switch
        {
            WindowBackdropType.Auto => Standard.DWMSBT.DWMSBT_TABBEDWINDOW,
            WindowBackdropType.TabbedWindow => Standard.DWMSBT.DWMSBT_TABBEDWINDOW,
            WindowBackdropType.MainWindow => Standard.DWMSBT.DWMSBT_MAINWINDOW,
            WindowBackdropType.TransientWindow => Standard.DWMSBT.DWMSBT_TRANSIENTWINDOW,
            _ => Standard.DWMSBT.DWMSBT_NONE
        };

        var dwmResult = NativeMethods.DwmSetWindowAttributeSystemBackdropType(hwnd, backdropPvAttribute);
        return dwmResult == HRESULT.S_OK;
    }

    private static bool RemoveBackdrop(IntPtr hwnd)
    {
        UpdateGlassFrame(hwnd, WindowBackdropType.None);

        var backdropPvAttribute = Standard.DWMSBT.DWMSBT_NONE;
        var dwmResult = NativeMethods.DwmSetWindowAttributeSystemBackdropType(hwnd, backdropPvAttribute);
        return dwmResult == HRESULT.S_OK;
    }

    private static bool RemoveBackground(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            var windowSource = HwndSource.FromHwnd(hwnd);
            if (windowSource.CompositionTarget != null)
            {
                // TODO : Save the previous background color and reapply in RestoreBackground 
                windowSource.CompositionTarget.BackgroundColor = Colors.Transparent;
                return true;
            }
        }
        return false;
    }

    private static bool RestoreBackground(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            var windowSource = HwndSource.FromHwnd(hwnd);
            if (windowSource?.Handle != IntPtr.Zero && windowSource.CompositionTarget != null)
            {
                windowSource.CompositionTarget.BackgroundColor = SystemColors.WindowColor;
                return true;
            }
        }
        return false;
    }

    private static bool UpdateGlassFrame(IntPtr hwnd, WindowBackdropType backdropType)
    {
        MARGINS margins = new MARGINS();
        if (backdropType != WindowBackdropType.None)
        {
            margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        }

        return NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    #endregion

    #region Internal Properties

    // On Windows the backdrop needs Win11 22H2+ (DWM system-backdrop attribute); off-Windows the
    // WebGPU compositor provides the backdrop natively (NSVisualEffectView on macOS), so it's
    // enabled there too. The app-context switch disables it on every platform.
    internal static bool IsBackdropEnabled => _isBackdropEnabled ??=
        (Utility.IsWindows11_22H2OrNewer || !OperatingSystem.IsWindows()) &&
        !FrameworkAppContextSwitches.DisableFluentThemeWindowBackdrop;

    private static bool? _isBackdropEnabled = null;

    #endregion

}