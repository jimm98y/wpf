// The seam that lets content which is NOT a WinForms control contribute to the host's frame — today
// a WPF element tree hosted by ElementHost (see the wpf-in-winforms sample). The per-OS hosts
// (Win32Host / CocoaHost) ask this for extra scenes on every present and append them AFTER the
// driver's own window scenes, so the hosted content draws on top of the control it occupies and the
// whole window is still ONE WebGPU render pass over ONE surface.
//
// Nothing here is WPF-specific and nothing is registered by default: in a pure WinForms app (the
// winforms-webgpu-gallery) every hook is null and the host behaves exactly as before.

using System;
using System.Collections.Generic;

internal static class EmbeddedScenes
{
    // Extra scenes for this frame, in the host's FORM-point space — the same space the driver's
    // window scenes are handed to WgpuPresenter in. Called with the form origin (ox, oy) in the
    // driver's screen space, so a contributor can map its own screen rect the way the driver does.
    internal static Func<int, int, List<(object Scene, int X, int Y)>> Collect;

    // A counter the contributor bumps whenever its content changes. The hosts present-on-change
    // (they skip the frame when the driver's paint version is unchanged), so without this a WPF-only
    // animation inside an ElementHost would never reach the screen.
    internal static Func<int> Version;

    // The host's native window and backing scale, published once the window exists. A contributor
    // that needs a real OS window of its own (ElementHost parents WPF's HwndSource here) has no
    // other way to reach them — the driver's Hwnds are managed handles, not OS windows.
    internal static IntPtr HostWindow;
    internal static float HostScale = 1f;
    internal static Action HostWindowReady;    // raised when the two above become valid

    internal static void PublishHostWindow(IntPtr window, float scale)
    {
        HostWindow = window;
        HostScale = scale;
        HostWindowReady?.Invoke();
    }

    internal static List<(object Scene, int X, int Y)> Get(int ox, int oy)
        => Collect?.Invoke(ox, oy);

    internal static int CurrentVersion() => Version == null ? 0 : Version();
}
