// The per-platform windowing shell for the WinForms-on-WebGPU host. Each platform (Cocoa, Win32,
// browser) creates a native window + event pump and a wgpu surface, then drives the SHARED present
// path (WgpuPresenter). Host.Main picks the implementation by OS; everything below the surface —
// the driver, the scene recorder, the WebGPU present — is identical across platforms.

internal interface IWinFormsHost
{
    void Show();                              // create the native window + wgpu surface
    void Present();                           // reflect current UI state (present-on-change)
    bool Pump();                              // drain OS events -> driver input; false when closing
    void InjectClickScreen(int x, int y);     // inject a click (self-test)
    void SaveFrame(string path);              // save the current frame to a PNG (verification)
}
