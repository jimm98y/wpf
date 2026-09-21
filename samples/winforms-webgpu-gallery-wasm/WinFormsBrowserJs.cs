// [JSImport] bindings into winforms-interop.js: creates the <canvas> the driver's scene is
// presented onto (registered in globalThis.__wpfCanvases so BrowserInterop.CreateSurface can
// find it) and drains queued DOM input each pump tick. Mirrors the WgpuBrowserJs pattern.

using System.Runtime.InteropServices.JavaScript;

internal static partial class WinFormsBrowserJs
{
    private const string ModuleName = "winformsInterop";

    // Create the presentation canvas (css size = form points; backing = points*dpr), register it in
    // globalThis.__wpfCanvases under the returned handle, and install DOM input listeners.
    [JSImport("createCanvas", ModuleName)]
    internal static partial int CreateCanvas(int widthPoints, int heightPoints);

    // Device-pixel ratio the canvas backing was sized to (the wgpu surface scale).
    [JSImport("dpr", ModuleName)]
    internal static partial double Dpr();

    // Resize the presentation canvas (points; backing scaled by dpr) so popups extending past the form fit.
    [JSImport("resizeCanvas", ModuleName)]
    internal static partial void ResizeCanvas(int widthPoints, int heightPoints);

    // JSON array of queued DOM events since the last call (mouse/key), or "" when empty.
    [JSImport("drainEvents", ModuleName)]
    internal static partial string DrainEvents();
}
