// Platform-NEUTRAL WebGPU present path for the WinForms host. Given a wgpu context + a
// presentable surface (created per-platform: CAMetalLayer on macOS, HWND on Windows, canvas in
// the browser), it presents the driver's composited control bitmap each frame by:
//   composite RGBA -> ImageBrush -> full-window textured quad -> WgpuSceneRenderer.RenderSceneToView
//   -> wgpuSurfacePresent.
// The API, WGSL shaders, renderer and this loop are identical on mac/win/browser; ONLY surface
// creation and the OS windowing host differ. This is the "controls presented via WebGPU" tier
// (the composite pixels still come from libgdiplus for now; GPU rasterization is the later swap).

using System;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

internal sealed unsafe class WgpuPresenter : IDisposable
{
    private readonly WgpuContext _ctx;
    private readonly IntPtr _surface;
    private readonly WgpuSceneRenderer _renderer;
    private readonly WGPUTextureFormat _format;
    private int _width, _height;
    private byte[] _rgba;

    internal WgpuPresenter(WgpuContext ctx, IntPtr surface, int width, int height)
    {
        _ctx = ctx;
        _surface = surface;
        _width = width;
        _height = height;
        _format = ChooseFormat(surface, ctx.Adapter);
        Configure();
        _renderer = new WgpuSceneRenderer(ctx);
    }

    internal WGPUTextureFormat Format => _format;

    // Upload the composite bitmap and present it as a full-window quad through the swap chain.
    internal bool Present(Bitmap composite)
    {
        if (composite.Width != _width || composite.Height != _height)
        {
            _width = composite.Width;
            _height = composite.Height;
            Configure(); // window resized -> resize the swap chain
        }

        byte[] rgba = ToRgba(composite);

        var root = new SceneVisual();
        root.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, _width, _height)),
            new ImageBrush(rgba, _width, _height)));

        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
            Console.Error.WriteLine($"[wgpu] GetCurrentTexture status={st.status} texture=0x{st.texture:x}");
        if (st.texture == IntPtr.Zero) return false;
        if (st.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
            st.status == WGPUSurfaceGetCurrentTextureStatus.Timeout)
        {
            Configure();
            return false;
        }
        if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.Occluded)
        {
            return false;
        }

        IntPtr view = wgpuTextureCreateView(st.texture, IntPtr.Zero);
        // Opaque white background matches the WinForms form fill (the composite already paints it).
        _renderer.RenderSceneToView(root, view, _format, _width, _height, RgbaColor.FromBytes(255, 255, 255, 255));
        bool ok = wgpuSurfacePresent(_surface) == WGPUStatus.Success;

        wgpuTextureViewRelease(view);
        wgpuTextureRelease(st.texture);
        return ok;
    }

    // System.Drawing 32bppArgb is B,G,R,A in memory (little-endian); ImageBrush wants R,G,B,A.
    private byte[] ToRgba(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, n = w * h * 4;
        if (_rgba == null || _rgba.Length != n) _rgba = new byte[n];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)bd.Scan0;
            fixed (byte* dstp = _rgba)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* srow = src + y * bd.Stride;
                    byte* drow = dstp + y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        drow[i + 0] = srow[i + 2]; // R <- R
                        drow[i + 1] = srow[i + 1]; // G
                        drow[i + 2] = srow[i + 0]; // B <- (BGRA)
                        drow[i + 3] = srow[i + 3]; // A
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return _rgba;
    }

    // Render the composite through WebGPU to an offscreen RGBA buffer (same ImageBrush quad as the
    // on-screen present, minus the swap chain) so the GPU output can be read back and verified —
    // screenshotting the live window needs screen-recording permission this env lacks.
    internal byte[] RenderCompositeToRgba(Bitmap composite)
    {
        int w = composite.Width, h = composite.Height;
        byte[] rgba = ToRgba(composite);
        var root = new SceneVisual();
        root.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, w, h)),
            new ImageBrush(rgba, w, h)));
        return _renderer.RenderToRgba(root, w, h, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private void Configure()
    {
        var config = new WGPUSurfaceConfiguration
        {
            device = _ctx.Device,
            format = _format,
            usage = WGPUTextureUsage.RenderAttachment,
            width = (uint)_width,
            height = (uint)_height,
            alphaMode = WGPUCompositeAlphaMode.Auto,
            presentMode = WGPUPresentMode.Fifo,
        };
        wgpuSurfaceConfigure(_surface, &config);
    }

    private static WGPUTextureFormat ChooseFormat(IntPtr surface, IntPtr adapter)
    {
        WGPUSurfaceCapabilities caps;
        if (wgpuSurfaceGetCapabilities(surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
            return WGPUTextureFormat.BGRA8Unorm;

        WGPUTextureFormat chosen = caps.formats[0];
        for (nuint i = 0; i < caps.formatCount; i++)
        {
            // Prefer a non-sRGB BGRA/RGBA swap format so the composite's bytes present 1:1
            // (the WinForms composite is already in display-space, not linear).
            if (caps.formats[i] == WGPUTextureFormat.BGRA8Unorm || caps.formats[i] == WGPUTextureFormat.RGBA8Unorm)
            {
                chosen = caps.formats[i];
                break;
            }
        }
        wgpuSurfaceCapabilitiesFreeMembers(caps);
        return chosen;
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _ctx?.Dispose();
    }
}
