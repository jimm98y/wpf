// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS presentation spike: renders the shared WPF-style scene through the WebGPU swap chain onto a
// UIView, i.e. the iOS analog of the Win32 SurfaceDemo and of CocoaWindow's CAMetalLayer path.
//
// The only genuinely platform-specific part is obtaining the CAMetalLayer: on macOS MacInterop must
// make an NSView layer-BACKED and add the CAMetalLayer as an autoresizing SUBLAYER, but on iOS a
// UIView can *be* Metal-backed by overriding +layerClass, so the view's own layer is the surface and
// Core Animation keeps its geometry in sync for free. Everything after that -- the
// WGPUSurfaceSourceMetalLayer chain, configure, acquire, render, present -- is identical to desktop.
//

using System;
using System.Numerics;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace IosSpike;

[Register ("MetalView")]
public sealed unsafe class MetalView : UIView
{
    private WgpuContext? _ctx;
    private WgpuSceneRenderer? _renderer;
    private IntPtr _surface;
    private WGPUTextureFormat _format;
    private CADisplayLink? _link;
    private int _frame;
    private uint _cfgW, _cfgH;

    /// <summary>Makes the view's own backing layer a CAMetalLayer (the iOS shortcut over AppKit).</summary>
    [Export ("layerClass")]
    public static Class GetLayerClass () => new Class (typeof (CAMetalLayer));

    public string Status { get; private set; } = "(not started)";

    public MetalView (CGRect frame) : base (frame) { }

    private CAMetalLayer MetalLayer => (CAMetalLayer)Layer!;

    public void Start ()
    {
        try {
            _ctx = WgpuContext.Create ();

            // Through the shared platform seam (NativePlatform -> IosInterop), not a local copy:
            // this is the same call CocoaWindow/HwndTarget make on macOS, so it exercises the real
            // M2 code path. It takes the UIView*, and reads the CAMetalLayer off it.
            _surface = NativePlatform.CreateWindowSurface (_ctx.Instance, Handle.Handle);
            if (_surface == IntPtr.Zero) { Status = "createSurface returned NULL"; return; }

            _format = ChooseFormat (_surface, _ctx.Adapter);
            _renderer = new WgpuSceneRenderer (_ctx);
            Configure ();

            Status = $"device 0x{_ctx.Device:x}\nsurface 0x{_surface:x}\nformat {_format}";

            _link = CADisplayLink.Create (OnFrame);
            _link.AddToRunLoop (NSRunLoop.Main, NSRunLoopMode.Common);
        } catch (Exception ex) {
            Status = $"{ex.GetType ().Name}\n{ex.Message}";
        }
    }

    private static WGPUTextureFormat ChooseFormat (IntPtr surface, IntPtr adapter)
    {
        var caps = new WGPUSurfaceCapabilities ();
        if (wgpuSurfaceGetCapabilities (surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
            return WGPUTextureFormat.BGRA8Unorm;

        WGPUTextureFormat chosen = caps.formats[0];
        for (nuint i = 0; i < caps.formatCount; i++) {
            if (caps.formats[i] == WGPUTextureFormat.BGRA8Unorm) { chosen = WGPUTextureFormat.BGRA8Unorm; break; }
        }
        return chosen;
    }

    /// <summary>Size the swap chain in DEVICE PIXELS (points * contentsScale), like the mac path.</summary>
    private void Configure ()
    {
        nfloat scale = UIScreen.MainScreen.NativeScale;
        MetalLayer.ContentsScale = scale;
        _cfgW = (uint)Math.Max (1, (int)(Bounds.Width * scale));
        _cfgH = (uint)Math.Max (1, (int)(Bounds.Height * scale));
        MetalLayer.DrawableSize = new CGSize (_cfgW, _cfgH);

        var config = new WGPUSurfaceConfiguration {
            device = _ctx!.Device,
            format = _format,
            usage = WGPUTextureUsage.RenderAttachment,
            width = _cfgW,
            height = _cfgH,
            alphaMode = WGPUCompositeAlphaMode.Auto,
            presentMode = WGPUPresentMode.Fifo,
        };
        wgpuSurfaceConfigure (_surface, &config);
    }

    private void OnFrame ()
    {
        if (_renderer == null || _surface == IntPtr.Zero) return;

        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture (_surface, &st);

        if (st.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
            st.status == WGPUSurfaceGetCurrentTextureStatus.Timeout) {
            Configure ();
            return;
        }
        if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
            st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal) {
            Status = $"GetCurrentTexture = {st.status}";
            return;
        }

        IntPtr view = wgpuTextureCreateView (st.texture, IntPtr.Zero);
        _renderer.RenderSceneToView (BuildScene (_frame++), view, _format, (int)_cfgW, (int)_cfgH,
                                     RgbaColor.FromBytes (255, 255, 255, 255));
        wgpuSurfacePresent (_surface);

        wgpuTextureViewRelease (view);
        wgpuTextureRelease (st.texture);
    }

    /// <summary>Static red + green squares plus a sliding blue bar, so motion is visible.</summary>
    private SceneVisual BuildScene (int frame)
    {
        float w = _cfgW, h = _cfgH;
        var root = new SceneVisual ();

        var red = new SceneVisual { Offset = new Vector2 (w * 0.10f, h * 0.12f) };
        red.Content.Add (new GeometryFill (new RectangleGeometry (new Rect (0, 0, w * 0.35f, h * 0.12f)),
            RgbaColor.FromBytes (220, 40, 40, 255)));
        root.Children.Add (red);

        var green = new SceneVisual { Offset = new Vector2 (w * 0.50f, h * 0.12f) };
        green.Content.Add (new GeometryFill (new RectangleGeometry (new Rect (0, 0, w * 0.35f, h * 0.12f)),
            RgbaColor.FromBytes (40, 190, 90, 255)));
        root.Children.Add (green);

        float t = (frame % 120) / 120f;
        var blue = new SceneVisual { Offset = new Vector2 (w * 0.10f + t * w * 0.6f, h * 0.30f) };
        blue.Content.Add (new GeometryFill (new RectangleGeometry (new Rect (0, 0, w * 0.18f, h * 0.10f)),
            RgbaColor.FromBytes (50, 90, 230, 255)));
        root.Children.Add (blue);

        return root;
    }
}
