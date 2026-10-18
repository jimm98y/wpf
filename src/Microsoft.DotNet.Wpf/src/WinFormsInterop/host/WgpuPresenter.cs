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
    private IntPtr _surface;
    // Every frame is bracketed with BeginFrame/EndFrame, as the WPF sink brackets its own: that is
    // the renderer's clock, and EndFrame is where its caches let go of what has not been drawn for a
    // while -- image textures, coverage masks, gradient ramps, cached layers. The WinForms presenter
    // never called them, so a window's renderer kept every texture it had ever made: every bitmap a
    // control drew, at every size a resize passed through. GraphWorX's private bytes rose by about
    // 27 MB per move+resize cycle for good, and the user's session reached 1.5 GB.
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
    // Never past the GPU's largest texture (WebGPU's default limit, which every adapter meets): a
    // system-aware window as wide as a 4K display at 96 dpi is 9,600 pixels wide on a 240-dpi one,
    // and configuring a surface that size aborted the process inside wgpu.
    internal const int MaxDeviceDimension = 8192;
    internal int DeviceWidth => System.Math.Min(MaxDeviceDimension, (int)System.Math.Round(_width * _scale));
    internal int DeviceHeight => System.Math.Min(MaxDeviceDimension, (int)System.Math.Round(_height * _scale));

    /// <summary>The native window the surface was made on (an HWND on Windows): every configure and
    /// frame checks it is still there. Zero when the host does not say.</summary>
    internal IntPtr NativeWindow { get; }

    internal WgpuPresenter(WgpuContext ctx, IntPtr surface, int width, int height, double scale = 1.0, bool srgb = false, IntPtr nativeWindow = default)
    {
        _ctx = ctx;
        NativeWindow = nativeWindow;
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
        // KERNING: Windows kerns a string run, and this presenter is what draws every WinForms
        // caption. It had the plain shaper while the composition sink had the kerning one, so the
        // measurements agreed with Windows and the pixels did not.
        _renderer = new WgpuSceneRenderer(ctx, LoadFont(), new KerningTextShaper());
        _partial = new WgpuSceneRenderer.PartialTarget(_renderer, " wf");
    }

    // Partial redraw: the frames are rendered into a texture that keeps the previous one, only where
    // the scenes changed, and copied to the swap chain (see WgpuSceneRenderer.PartialTarget).
    private readonly WgpuSceneRenderer.PartialTarget _partial;

    /// <summary>The last scene present's damage, for frame traces: "full" or "N rects, P px".</summary>
    /// <summary>Make the next present a full one.</summary>
    internal void InvalidateFrame() => _partial.Invalidate();

    /// <summary>Where the last scene present's time went (frame traces).</summary>
    internal string LastTiming { get; private set; } = "";

    internal string LastDamage => _partial.LastFull ? "full" : $"{_partial.LastRects} rects {_partial.LastPixels} px";

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
    private bool PresentLayersCore(IReadOnlyList<WgpuLayer> layers, Rectangle? caret, int surfaceW, int surfaceH)
    {
        if (surfaceW != _width || surfaceH != _height)
        {
            _width = surfaceW;
            _height = surfaceH;
            ConfigureNow(); // window resized -> resize the swap chain
        }

        SceneVisual root = BuildScene(layers, caret);

        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        if (Environment.GetEnvironmentVariable("WF_TRACE") != null)
            Console.Error.WriteLine($"[wgpu] GetCurrentTexture status={st.status} texture=0x{st.texture:x}");
        // On macOS the first frames (window not yet front-most) return Occluded with a NULL texture;
        // wgpuTextureCreateView(null) panics wgpu ("invalid texture", non-unwinding abort). Skip them.
        if (st.texture == IntPtr.Zero) return NoDrawable();
        _nullAcquires = 0;
        if (st.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
            st.status == WGPUSurfaceGetCurrentTextureStatus.Timeout)
        {
            ConfigureNow();
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
        _renderer.BeginFrame();
        try
        {
            _renderer.RenderSceneToView(root, view, _format, _width, _height, RgbaColor.FromBytes(255, 255, 255, 255));
        }
        finally { _renderer.EndFrame(); }   // lets the caches let go (see the note at _renderer)
        bool ok = PresentAndRelease(_surface, st.texture) == WGPUStatus.Success;   // releases the texture

        wgpuTextureViewRelease(view);
        return ok;
    }

    // GPU-RASTER present: composite each window's RECORDED scene (built by the System.Drawing
    // recorder, positioned in form space) plus the caret into ONE root scene and render it in a single
    // pass to the surface. No per-control readback / re-upload — the scenes go straight to the GPU on
    // this one device. `scenes` items are (boxed SceneVisual, x, y) in the driver's paint order.
    private static readonly bool s_tracePresentStatus =
        Environment.GetEnvironmentVariable("WF_TRACE_SURFACE") == "1";

    // A surface configured while its window was not on screen yet -- created behind another Space,
    // before its first order-front, or by a busy main thread for a form on a thread of its own --
    // can go on returning no drawable (Occluded) after the window shows, and a window that never
    // gets a drawable stays the colour AppKit cleared it to. Only Outdated and Timeout reconfigure,
    // so do it after a run of drawable-less acquires too: the swap chain re-attaches to the layer,
    // now on screen. The WPF side does the same (WpfCompositionSink.NoDrawable).
    private int _nullAcquires;

    /// <summary>Told apart in the surface traces (" popups" for a form's popup layer).</summary>
    internal string Tag = "";

    private bool NoDrawable()
    {
        if (++_nullAcquires % 30 == 0)
        {
            if (s_tracePresentStatus) Console.WriteLine($"surface{Tag}: no drawable {_nullAcquires} times in a row; reconfiguring {_width}x{_height}");
            ConfigureNow();
        }
        return false;
    }

    internal bool PresentScenes(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret, int surfaceW, int surfaceH)
        => PresentScenes(scenes, caret, null, surfaceW, surfaceH);

    private bool PresentScenesCore(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret,
                                IReadOnlyList<(Rectangle Rect, int Width)> rubberBands, int surfaceW, int surfaceH)
    {
        if (surfaceW != _width || surfaceH != _height) { _width = surfaceW; _height = surfaceH; ConfigureNow(); }

        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SceneVisual root = BuildRoot(scenes, caret, rubberBands);

        long p1 = System.Diagnostics.Stopwatch.GetTimestamp();
        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        long p2 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (st.texture == IntPtr.Zero) return NoDrawable();
        _nullAcquires = 0;
        if (st.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
            st.status == WGPUSurfaceGetCurrentTextureStatus.Timeout) { ConfigureNow(); return false; }
        if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.Occluded) return false;

        IntPtr view = wgpuTextureCreateView(st.texture, IntPtr.Zero);
        _renderer.BeginFrame();
        try
        {
            _partial.Render(root, view, _format, DeviceWidth, DeviceHeight, ClearColor, false);
        }
        finally { _renderer.EndFrame(); }   // lets the caches let go (see the note at _renderer)
        long p3 = System.Diagnostics.Stopwatch.GetTimestamp();
        WGPUStatus presented = PresentAndRelease(_surface, st.texture);   // releases the texture
        long p4 = System.Diagnostics.Stopwatch.GetTimestamp();
        double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTiming = $"build {Ms(p0, p1):0.0} acquire {Ms(p1, p2):0.0} render+submit {Ms(p2, p3):0.0} present {Ms(p3, p4):0.0} ms at {DeviceWidth}x{DeviceHeight} {PresentMode} [{_partial.LastSplit}]";
        bool ok = presented == WGPUStatus.Success;
        // A window that shows the CLEAR COLOUR and nothing else looks exactly like a renderer that
        // drew nothing, and it is not: the scenes can all be built and rendered correctly and still
        // never reach the glass. Say which of the two happened rather than leaving it to be guessed.
        if (s_tracePresentStatus)
            Console.WriteLine($"surface{Tag}: acquire={st.status} present={presented} "
                              + $"{DeviceWidth}x{DeviceHeight} format={_format}");
        wgpuTextureViewRelease(view);
        return ok;
    }

    /// <summary>
    /// The window has just been given another size: show the last frame in it straight away, at its
    /// top left, before the new one is made. A resized GL swap chain shows nothing -- black -- until
    /// something is presented to it, and the new frame is the application's layout and repaint first:
    /// every step of a live resize of GraphWorX was black for the 100 ms and more that took. False
    /// when there is no last frame to show.
    /// </summary>
    private bool PresentLastAtSizeCore(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (width != _width || height != _height) { _width = width; _height = height; ConfigureNow(); }
        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(_surface, &st);
        if (st.texture == IntPtr.Zero) return false;
        if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)
        {
            wgpuTextureRelease(st.texture);
            return false;
        }
        IntPtr view = wgpuTextureCreateView(st.texture, IntPtr.Zero);
        bool drawn = _partial.PresentLast(view, _format, DeviceWidth, DeviceHeight, ClearColor);
        if (drawn) drawn = PresentAndRelease(_surface, st.texture) == WGPUStatus.Success;   // releases the texture
        else wgpuTextureRelease(st.texture);   // never presented: the release discards the acquired image
        wgpuTextureViewRelease(view);
        return drawn;
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
        => _renderer.RenderToRgba(BuildRoot(scenes, caret), DeviceWidth, DeviceHeight, ClearColor, srgbOutput: _srgb);

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

    // A window that is see-through where nothing is drawn: a layered window, whose picture carries
    // its own alpha (a splash screen with soft edges). Cleared to transparent rather than white, and
    // the surface composited with its alpha rather than as opaque.
    private bool _transparent;

    internal void SetTransparent(bool transparent)
    {
        if (_transparent == transparent) return;
        _transparent = transparent;
        ClearColor = transparent ? RgbaColor.FromBytes(0, 0, 0, 0) : RgbaColor.FromBytes(255, 255, 255, 255);
        Configure();
    }

    // The renderer writes premultiplied colour; take the mode that says so if the surface has it.
    private WGPUCompositeAlphaMode TransparentAlphaMode()
    {
        WGPUSurfaceCapabilities caps;
        if (wgpuSurfaceGetCapabilities(_surface, _ctx.Adapter, &caps) != WGPUStatus.Success) return WGPUCompositeAlphaMode.Auto;
        var mode = WGPUCompositeAlphaMode.Auto;
        for (nuint i = 0; i < caps.alphaModeCount; i++)
        {
            if (caps.alphaModes[i] == WGPUCompositeAlphaMode.Premultiplied) { mode = WGPUCompositeAlphaMode.Premultiplied; break; }
            if (caps.alphaModes[i] == WGPUCompositeAlphaMode.Unpremultiplied) mode = WGPUCompositeAlphaMode.Unpremultiplied;
        }
        wgpuSurfaceCapabilitiesFreeMembers(caps);
        return mode;
    }

    // ONE FRAME AT A TIME. A surface holds at most one acquired image, and wgpu-native aborts the
    // process -- a panic that cannot unwind -- on anything that assumes otherwise: a second acquire
    // ("already acquired"), a configure while an image is out ("previous output exists"), a release
    // of a texture whose image has gone. So every frame of this surface runs under one guard: a frame
    // asked for from inside another (a message pumped during the present, a resize arriving in the
    // middle of one) is refused -- the caller sees false and presents again -- and a reconfigure
    // asked for then waits for the frame to finish. Another thread waits its turn.
    private readonly object _frameLock = new object();
    private bool _inFrame;
    private bool _configurePending;

    /// <summary>Frames refused because another frame of the surface was in progress (tests, traces).</summary>
    internal int RefusedNestedFrames { get; private set; }

    private bool Frame(Func<bool> body)
    {
        lock (_frameLock)
        {
            if (_inFrame) { RefusedNestedFrames++; return false; }
            if (_disposed) return false;
            // Nothing to present to once the window has gone (and a present to it would fail).
            if (!Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.IsWindowAlive(NativeWindow)) return false;
            _inFrame = true;
            try
            {
                if (_configurePending) { _configurePending = false; ConfigureNow(); }
                return body();
            }
            finally
            {
                _inFrame = false;
                if (_disposePending) Dispose();
                else if (_configurePending) { _configurePending = false; ConfigureNow(); }
            }
        }
    }

    internal bool PresentLayers(IReadOnlyList<WgpuLayer> layers, Rectangle? caret, int surfaceW, int surfaceH)
        => Frame(() => PresentLayersCore(layers, caret, surfaceW, surfaceH));

    internal bool PresentScenes(IReadOnlyList<(object Scene, int X, int Y)> scenes, Rectangle? caret,
                                IReadOnlyList<(Rectangle Rect, int Width)> rubberBands, int surfaceW, int surfaceH)
        => Frame(() => PresentScenesCore(scenes, caret, rubberBands, surfaceW, surfaceH));

    internal bool PresentLastAtSize(int width, int height)
        => Frame(() => PresentLastAtSizeCore(width, height));

    /// <summary>Reconfigure now, or -- in the middle of a frame -- as soon as it is over.</summary>
    private void Configure()
    {
        lock (_frameLock)
        {
            if (_inFrame) { _configurePending = true; return; }
            ConfigureNow();
        }
    }

    private void ConfigureNow()
    {
        if (_disposed || _surface == IntPtr.Zero) return;
        // The window has gone: its surface cannot be configured (wgpu-native aborts, "Invalid surface").
        if (!Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.IsWindowAlive(NativeWindow)) return;
        var config = new WGPUSurfaceConfiguration
        {
            device = _ctx.Device,
            format = _format,
            usage = WGPUTextureUsage.RenderAttachment,
            // Never zero: configuring a zero-area surface is fatal in wgpu-native, and a window can
            // be sized to nothing (a collapsed panel, a minimised form) at any time.
            width = (uint)System.Math.Max(1, DeviceWidth),     // the CAMetalLayer drawable is device-pixel sized
            height = (uint)System.Math.Max(1, DeviceHeight),
            alphaMode = _transparent ? TransparentAlphaMode() : WGPUCompositeAlphaMode.Auto,
            presentMode = _presentMode ??= ChoosePresentMode(),
        };
        wgpuSurfaceConfigure(_surface, &config);
    }

    private WGPUPresentMode? _presentMode;

    /// <summary>
    /// How frames reach the screen. FIFO makes every present wait for the display, and the present is
    /// made on the UI thread: each frame put up to a refresh interval of waiting in front of the
    /// next input message. A windowed swap chain on Windows is composed by DWM, which shows the newest
    /// frame at its own vsync -- a present that does not wait cannot tear there -- so on Windows the
    /// host asks for MAILBOX, else IMMEDIATE, else FIFO. Frames are made on demand (input, a timer, an
    /// animation's own clock), so not waiting does not mean presenting more. WF_PRESENT=fifo |
    /// mailbox | immediate chooses.
    /// </summary>
    private WGPUPresentMode ChoosePresentMode()
    {
        string want = Environment.GetEnvironmentVariable("WF_PRESENT")?.ToLowerInvariant();
        WGPUPresentMode[] order = want switch
        {
            "fifo" => new[] { WGPUPresentMode.Fifo },
            "mailbox" => new[] { WGPUPresentMode.Mailbox, WGPUPresentMode.Fifo },
            "immediate" => new[] { WGPUPresentMode.Immediate, WGPUPresentMode.Fifo },
            _ => OperatingSystem.IsWindows()
                ? new[] { WGPUPresentMode.Mailbox, WGPUPresentMode.Immediate, WGPUPresentMode.Fifo }
                : new[] { WGPUPresentMode.Fifo },
        };
        WGPUSurfaceCapabilities caps;
        if (wgpuSurfaceGetCapabilities(_surface, _ctx.Adapter, &caps) != WGPUStatus.Success) return WGPUPresentMode.Fifo;
        try
        {
            foreach (WGPUPresentMode m in order)
                for (nuint i = 0; i < caps.presentModeCount; i++)
                    if (caps.presentModes[i] == m) return m;
            return WGPUPresentMode.Fifo;
        }
        finally { wgpuSurfaceCapabilitiesFreeMembers(caps); }
    }

    /// <summary>The present mode the surface was configured with (traces, tests).</summary>
    internal string PresentMode => (_presentMode ?? WGPUPresentMode.Fifo).ToString();

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

    /// <summary>Everything the window had on the GPU: its frames, its surface and its device. A
    /// window that closed used to keep all of it -- each dialog an application opened and closed
    /// leaked a device, and on the GL backend a GL context behind a hidden window. Not while a frame
    /// is out (the frame guard): a present asked for in the middle of this one is refused.</summary>
    public void Dispose()
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            // Asked from inside a frame (a message pumped during the present): taken apart once that
            // frame is over, not from under it.
            if (_inFrame) { _disposePending = true; return; }
            _disposed = true;
            _inFrame = true;          // no frame may start on what is being taken apart
            _partial?.Dispose();
            _renderer?.Dispose();
            if (_surface != IntPtr.Zero) { wgpuSurfaceRelease(_surface); _surface = IntPtr.Zero; }
            _ctx?.Dispose();
        }
    }

    private bool _disposed, _disposePending;
}
