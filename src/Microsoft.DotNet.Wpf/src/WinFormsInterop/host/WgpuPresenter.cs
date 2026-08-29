// Platform-NEUTRAL WebGPU present path for the WinForms host. Given a wgpu context + a
// presentable surface (created per-platform: CAMetalLayer on macOS, HWND on Windows, canvas in
// the browser), it presents the driver's windows each frame as a set of positioned textured quads:
//   each control window's backing bitmap -> RGBA -> ImageBrush -> a quad at the window's position;
//   the GPU composites the layers (form + child controls + WS_POPUP dropdowns) and the caret ->
//   WgpuSceneRenderer.RenderSceneToView -> wgpuSurfacePresent.
// The API, WGSL shaders, renderer and this loop are identical on mac/win/browser; ONLY surface
// creation and the OS windowing host differ. This is the "controls presented via WebGPU" tier
// (each control is its own GPU layer; the pixels still come from libgdiplus, GPU raster is next).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

// One control window to composite: its backing bitmap and its top-left in surface (form) space.
internal readonly struct WgpuLayer
{
    public readonly Bitmap Bitmap;
    public readonly int X, Y;
    public WgpuLayer(Bitmap bitmap, int x, int y) { Bitmap = bitmap; X = x; Y = y; }
}

internal sealed unsafe class WgpuPresenter : IDisposable
{
    private readonly WgpuContext _ctx;
    private readonly IntPtr _surface;
    private readonly WgpuSceneRenderer _renderer;
    private readonly WGPUTextureFormat _format;
    private readonly bool _srgb;          // sRGB surface -> renderer applies gamma-correct text coverage
    private int _width, _height;          // logical (point) size
    private float _scale;                 // backing scale (2 on Retina): render at device pixels

    // Background the present clears to (the area outside the form/popups). Defaults to opaque white
    // (the desktop hosts present exactly form-sized, so it never shows); the browser host sets it to the
    // form's BackColor so the canvas area beyond the form — exposed when a popup grows the canvas — blends in.
    internal RgbaColor ClearColor = RgbaColor.FromBytes(255, 255, 255, 255);

    internal float Scale => _scale;
    // Update the backing scale at runtime (window moved to a different-DPI display) and resize the
    // swap chain to the new device-pixel size.
    internal void SetScale(double scale)
    {
        float s = (float)(scale > 0 ? scale : 1.0);
        if (s == _scale) return;
        _scale = s;
        Configure();
    }

    // Device (physical) pixel dimensions the surface is configured to.
    internal int DeviceWidth => (int)System.Math.Round(_width * _scale);
    internal int DeviceHeight => (int)System.Math.Round(_height * _scale);

    internal WgpuPresenter(WgpuContext ctx, IntPtr surface, int width, int height, double scale = 1.0, bool srgb = false)
    {
        _ctx = ctx;
        _surface = surface;
        _width = width;
        _height = height;
        _scale = (float)(scale > 0 ? scale : 1.0);
        // A GAMMA-SPACE compositor hands the surface colours that are ALREADY sRGB-encoded, so the
        // surface has to be a plain UNORM one that presents them byte for byte. Asking for an sRGB
        // surface makes the hardware decode on read and encode on write, and for an OPAQUE fill that
        // round trip is the identity -- which is why every background matched Windows exactly and this
        // went unnoticed for so long. It is NOT the identity for a BLEND: it makes every one of them a
        // linear-light blend. Text is nothing but blends, and a glyph edge at 0.72 coverage over white
        // came out at 145 where GDI, which blends in gamma space, puts it at 72. Measured against a
        // live stock window, our text carried about a quarter less ink than Windows' everywhere it
        // appeared -- while the same renderer read back through RenderToRgba was within a few percent,
        // because RenderToRgba already forces UNORM in this mode. This is that same rule, on the path
        // that reaches a screen.
        if (WgpuSceneRenderer.s_gammaComposite) srgb = false;
        _srgb = srgb;
        _format = ChooseFormat(surface, ctx.Adapter, srgb);
        Configure();
        // A real TrueType font so control text (lowercase) rasterizes; the default BuiltinBitmapFont
        // is uppercase-only. Glyphs are rasterized on the GPU at present time.
        _renderer = new WgpuSceneRenderer(ctx, LoadFont(), new SimpleTextShaper());
    }

    // The face the presenter rasterizes control text with. It MUST stay in step with the one
    // System.Drawing's GPU-raster backend measures with (TextMetrics.LoadFont) — the theme centres
    // and clips text using those measurements, so a different face here misplaces every label. The
    // Windows entry is not optional: without it this fell through to BuiltinBitmapFont, which is
    // uppercase-only, and every control label rendered as its first letter alone ("Fast" -> "F").
    private static IFont LoadFont()
    {
        foreach (string p in new[] { "/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf",
                                     "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                     "C:\\Windows\\Fonts\\arial.ttf", "C:\\Windows\\Fonts\\segoeui.ttf",
                                     "/fonts/Arial.ttf", "/fonts/LiberationSans-Regular.ttf" })   // browser VFS
            if (System.IO.File.Exists(p)) return new TrueTypeFont(System.IO.File.ReadAllBytes(p));
        return new BuiltinBitmapFont();
    }

    internal WGPUTextureFormat Format => _format;

    // Present the driver's windows as positioned GPU quads (form + children + popups), plus the
    // blinking caret as a solid quad. surfaceW/H = the form's size (the presentation origin).
    internal bool PresentLayers(IReadOnlyList<WgpuLayer> layers, Rectangle? caret, int surfaceW, int surfaceH)
    {
        if (surfaceW != _width || surfaceH != _height)
        {
            _width = surfaceW;
            _height = surfaceH;
            Configure(); // window resized -> resize the swap chain
        }

        SceneVisual root = BuildScene(layers, caret);

        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
            Console.Error.WriteLine($"[wgpu] GetCurrentTexture status={st.status} texture=0x{st.texture:x}");
        // On macOS the first frames (window not yet front-most) return Occluded with a NULL texture;
        // wgpuTextureCreateView(null) panics wgpu ("invalid texture", non-unwinding abort). Skip them.
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
        // Opaque white clear matches the desktop behind the form; the form quad paints over it.
        _renderer.RenderSceneToView(root, view, _format, _width, _height, RgbaColor.FromBytes(255, 255, 255, 255));
        bool ok = wgpuSurfacePresent(_surface) == WGPUStatus.Success;

        wgpuTextureViewRelease(view);
        wgpuTextureRelease(st.texture);
        return ok;
    }

    // GPU-RASTER present: composite each window's RECORDED scene (built by the System.Drawing
    // recorder, positioned in form space) plus the caret into ONE root scene and render it in a single
    // pass to the surface. No per-control readback / re-upload — the scenes go straight to the GPU on
    // this one device. `scenes` items are (boxed SceneVisual, x, y) in the driver's paint order.
    private static readonly bool s_tracePresentStatus =
        Environment.GetEnvironmentVariable("WF_TRACE_SURFACE") == "1";

    internal bool PresentScenes(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret, int surfaceW, int surfaceH)
        => PresentScenes(scenes, caret, null, surfaceW, surfaceH);

    internal bool PresentScenes(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret,
                                IReadOnlyList<(Rectangle Rect, int Width)> rubberBands, int surfaceW, int surfaceH)
    {
        if (surfaceW != _width || surfaceH != _height) { _width = surfaceW; _height = surfaceH; Configure(); }

        SceneVisual root = BuildRoot(scenes, caret, rubberBands);

        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        if (st.texture == IntPtr.Zero) return false;
        if (st.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
            st.status == WGPUSurfaceGetCurrentTextureStatus.Timeout) { Configure(); return false; }
        if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.Occluded) return false;

        IntPtr view = wgpuTextureCreateView(st.texture, IntPtr.Zero);
        _renderer.RenderSceneToView(root, view, _format, DeviceWidth, DeviceHeight, ClearColor);
        WGPUStatus presented = wgpuSurfacePresent(_surface);
        bool ok = presented == WGPUStatus.Success;
        // A window that shows the CLEAR COLOUR and nothing else looks exactly like a renderer that
        // drew nothing, and it is not: the scenes can all be built and rendered correctly and still
        // never reach the glass. Say which of the two happened rather than leaving it to be guessed.
        if (s_tracePresentStatus)
            Console.WriteLine($"surface: acquire={st.status} present={presented} "
                              + $"{DeviceWidth}x{DeviceHeight} format={_format}");
        wgpuTextureViewRelease(view);
        wgpuTextureRelease(st.texture);
        return ok;
    }

    private SceneVisual BuildRoot(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret)
        => BuildRoot(scenes, caret, null);

    private SceneVisual BuildRoot(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret,
                                  IReadOnlyList<(Rectangle Rect, int Width)> rubberBands)
    {
        var root = new SceneVisual();
        foreach ((object scene, int x, int y) in scenes)
        {
            if (scene is SceneVisual sv)
            {
                var wrap = new SceneVisual { Offset = new Vector2(x, y) };
                wrap.Children.Add(sv);
                root.Children.Add(wrap);
            }
        }
        // The caret must sit ON TOP of every control — add it as the LAST child, because a visual's
        // Content is drawn before its Children (so a caret in root.Content would render under the
        // control scenes and be hidden behind the TextBox's own background).
        if (caret is Rectangle c)
        {
            var caretVisual = new SceneVisual();
            caretVisual.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(c.X, c.Y, c.Width, c.Height)), RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(caretVisual);
        }
        // A rubber band -- a splitter being dragged, a selection being swept -- goes over
        // everything, the caret included: it is feedback about a gesture in progress, not part of
        // any window's content.
        if (rubberBands != null)
        {
            foreach ((Rectangle r, int width) in rubberBands)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                int w = width <= 0 ? 1 : width;
                var band = new SceneVisual();
                RgbaColor ink = RgbaColor.FromBytes(0, 0, 0, 255);
                // Four sides rather than a stroked outline: the renderer strokes from the centre
                // of the line, which for a one-pixel band lands on a half pixel and greys out.
                band.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(r.X, r.Y, r.Width, w)), ink));
                band.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(r.X, r.Bottom - w, r.Width, w)), ink));
                band.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(r.X, r.Y, w, r.Height)), ink));
                band.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(r.Right - w, r.Y, w, r.Height)), ink));
                root.Children.Add(band);
            }
        }

        // Render the point-space scene at device resolution (Retina): scale the whole tree by the
        // backing scale so glyphs (GPU-rasterized in device space) and geometry are crisp, not upscaled.
        root.Transform = Matrix3x2.CreateScale(_scale);
        return root;
    }

    // Offscreen readback of the composited scenes (for verification when a screenshot isn't available).
    internal byte[] RenderScenesToRgba(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret)
        => _renderer.RenderToRgba(BuildRoot(scenes, caret), DeviceWidth, DeviceHeight, RgbaColor.FromBytes(255, 255, 255, 255), srgbOutput: _srgb);

    // Offscreen readback of the same layered scene (minus the swap chain) so the GPU output can be
    // verified — screenshotting the live window needs screen-recording permission this env lacks.
    internal byte[] RenderLayersToRgba(IReadOnlyList<WgpuLayer> layers, Rectangle? caret)
    {
        SceneVisual root = BuildScene(layers, caret);
        return _renderer.RenderToRgba(root, _width, _height, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    // Build a scene: one image-brush quad per window (parents before children, popups last — the
    // caller supplies them in paint order), then the caret as a solid black quad on top.
    private SceneVisual BuildScene(IReadOnlyList<WgpuLayer> layers, Rectangle? caret)
    {
        var root = new SceneVisual();
        foreach (WgpuLayer layer in layers)
        {
            int w = layer.Bitmap.Width, h = layer.Bitmap.Height;
            var visual = new SceneVisual { Offset = new System.Numerics.Vector2(layer.X, layer.Y) };
            visual.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(0, 0, w, h)),
                new ImageBrush(ToRgba(layer.Bitmap), w, h)));
            root.Children.Add(visual);
        }
        if (caret is Rectangle c)
        {
            root.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(c.X, c.Y, c.Width, c.Height)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
        }
        return root;
    }

    // System.Drawing 32bppArgb is B,G,R,A in memory (little-endian); ImageBrush wants R,G,B,A.
    // Allocates a fresh buffer per call (each layer's ImageBrush holds its own until render).
    private static byte[] ToRgba(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var rgba = new byte[w * h * 4];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)bd.Scan0;
            fixed (byte* dstp = rgba)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* srow = src + y * bd.Stride;
                    byte* drow = dstp + y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        drow[i + 0] = srow[i + 2]; // R
                        drow[i + 1] = srow[i + 1]; // G
                        drow[i + 2] = srow[i + 0]; // B (from BGRA)
                        drow[i + 3] = srow[i + 3]; // A
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return rgba;
    }

    private void Configure()
    {
        var config = new WGPUSurfaceConfiguration
        {
            device = _ctx.Device,
            format = _format,
            usage = WGPUTextureUsage.RenderAttachment,
            width = (uint)DeviceWidth,     // the CAMetalLayer drawable is device-pixel sized
            height = (uint)DeviceHeight,
            alphaMode = WGPUCompositeAlphaMode.Auto,
            presentMode = WGPUPresentMode.Fifo,
        };
        wgpuSurfaceConfigure(_surface, &config);
    }

    private static WGPUTextureFormat ChooseFormat(IntPtr surface, IntPtr adapter, bool srgb)
    {
        WGPUSurfaceCapabilities caps;
        if (wgpuSurfaceGetCapabilities(surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
            return srgb ? WGPUTextureFormat.BGRA8UnormSrgb : WGPUTextureFormat.BGRA8Unorm;

        // sRGB (scene path): the renderer produces linear colour + applies WPF's gamma-correct glyph
        // coverage, so text/edges are crisp; the hardware sRGB-encodes on write. Non-sRGB (bitmap path):
        // the composite bytes are already display-space, present 1:1.
        WGPUTextureFormat[] pref = srgb
            ? new[] { WGPUTextureFormat.BGRA8UnormSrgb, WGPUTextureFormat.RGBA8UnormSrgb }
            : new[] { WGPUTextureFormat.BGRA8Unorm, WGPUTextureFormat.RGBA8Unorm };
        WGPUTextureFormat chosen = caps.formats[0];
        bool found = false;
        foreach (WGPUTextureFormat want in pref)
        {
            for (nuint i = 0; i < caps.formatCount; i++)
                if (caps.formats[i] == want) { chosen = want; found = true; break; }
            if (found) break;
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
