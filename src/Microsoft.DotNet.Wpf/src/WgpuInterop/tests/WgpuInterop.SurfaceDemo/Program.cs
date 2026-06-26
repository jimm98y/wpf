// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 HWND present demo: renders the WPF-style scene to a real on-screen
// window via the WebGPU swap chain -- the cross-platform analog of milcore
// presenting to an HwndTarget. It configures a surface over a Win32 HWND, then
// each frame acquires the swap-chain texture, renders the scene with the shared
// renderer and presents. The loop is bounded (frame count + wall-clock) so it
// can run unattended; it asserts that frames were actually acquired/presented.
//

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.SurfaceDemo
{
    internal static unsafe class Program
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int MaxFrames = 240;

        private static int Main(string[] args)
        {
            int maxFrames = MaxFrames;
            var budget = TimeSpan.FromSeconds(8);
            // Allow a quick automated pass (few frames) for CI-like runs.
            if (args.Length > 0 && int.TryParse(args[0], out int f) && f > 0) maxFrames = f;

            using var window = new Win32Window("WPF on WebGPU — HWND present", Width, Height);
            Console.WriteLine($"created HWND 0x{window.Hwnd:x}");

            using var ctx = WgpuContext.Create();
            Console.WriteLine($"device 0x{ctx.Device:x}");

            IntPtr surface = CreateHwndSurface(ctx.Instance, window);
            if (surface == IntPtr.Zero) return Fail("wgpuInstanceCreateSurface returned null");
            Console.WriteLine($"surface 0x{surface:x}");

            WGPUTextureFormat format = ChooseFormat(surface, ctx.Adapter);
            Console.WriteLine($"surface format = {format}");

            Configure(surface, ctx.Device, format);

            var renderer = new WgpuSceneRenderer(ctx);
            var background = RgbaColor.FromBytes(255, 255, 255, 255);

            int acquired = 0, presented = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < maxFrames && !window.QuitRequested && sw.Elapsed < budget; i++)
            {
                window.PumpMessages();

                WGPUSurfaceTexture surfaceTexture;
                wgpuSurfaceGetCurrentTexture(surface, &surfaceTexture);

                if (surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
                    surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Timeout)
                {
                    Configure(surface, ctx.Device, format); // window resized/lost; reconfigure
                    continue;
                }
                if (surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
                    surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)
                {
                    return Fail($"GetCurrentTexture status = {surfaceTexture.status} on frame {i}");
                }
                acquired++;

                IntPtr view = wgpuTextureCreateView(surfaceTexture.texture, IntPtr.Zero);
                renderer.RenderSceneToView(BuildScene(i), view, format, Width, Height, background);

                if (wgpuSurfacePresent(surface) == WGPUStatus.Success)
                    presented++;

                Thread.Sleep(8); // ~120 Hz cap; Fifo also paces to vblank
            }

            Console.WriteLine($"frames acquired = {acquired}, presented = {presented}, elapsed = {sw.ElapsedMilliseconds} ms");

            if (acquired > 0 && presented > 0)
            {
                Console.WriteLine("HWND PRESENT DEMO PASSED: scene rendered and presented through the WebGPU swap chain.");
                return 0;
            }
            return Fail("no frames were acquired/presented");
        }

        // A small animated scene: static red + semi-transparent green, plus a
        // blue rectangle that slides across, so motion + blending are visible.
        private static SceneVisual BuildScene(int frame)
        {
            var root = new SceneVisual();

            var red = new SceneVisual { Offset = new Vector2(40, 40) };
            red.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 120, 80)),
                RgbaColor.FromBytes(220, 40, 40, 255)));
            root.Children.Add(red);

            var green = new SceneVisual { Offset = new Vector2(90, 80), Opacity = 0.5 };
            green.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 120, 80)),
                RgbaColor.FromBytes(40, 200, 40, 255)));
            root.Children.Add(green);

            float x = 20 + (frame * 2 % (Width - 60));
            var blue = new SceneVisual { Offset = new Vector2(x, 160) };
            blue.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 40, 40)),
                RgbaColor.FromBytes(40, 80, 230, 255)));
            root.Children.Add(blue);

            return root;
        }

        private static IntPtr CreateHwndSurface(IntPtr instance, Win32Window window)
        {
            var hwndSource = new WGPUSurfaceSourceWindowsHWND
            {
                chain = new WGPUChainedStruct { next = null, sType = WGPUSType_SurfaceSourceWindowsHWND },
                hinstance = (void*)window.HInstance,
                hwnd = (void*)window.Hwnd,
            };
            var desc = new WGPUSurfaceDescriptor { nextInChain = (WGPUChainedStruct*)&hwndSource };
            return wgpuInstanceCreateSurface(instance, &desc);
        }

        private static WGPUTextureFormat ChooseFormat(IntPtr surface, IntPtr adapter)
        {
            WGPUSurfaceCapabilities caps;
            if (wgpuSurfaceGetCapabilities(surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
                return WGPUTextureFormat.BGRA8Unorm;

            WGPUTextureFormat chosen = caps.formats[0];
            for (nuint i = 0; i < caps.formatCount; i++)
            {
                if (caps.formats[i] == WGPUTextureFormat.BGRA8Unorm)
                {
                    chosen = WGPUTextureFormat.BGRA8Unorm; // common, preferred swap-chain format
                    break;
                }
            }
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return chosen;
        }

        private static void Configure(IntPtr surface, IntPtr device, WGPUTextureFormat format)
        {
            var config = new WGPUSurfaceConfiguration
            {
                device = device,
                format = format,
                usage = WGPUTextureUsage.RenderAttachment,
                width = Width,
                height = Height,
                alphaMode = WGPUCompositeAlphaMode.Auto,
                presentMode = WGPUPresentMode.Fifo,
            };
            wgpuSurfaceConfigure(surface, &config);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"HWND PRESENT DEMO FAILED: {message}");
            return 1;
        }
    }
}
