// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A present that fails must not take the process with it.
//
// wgpu-native's release of a surface texture that was never presented discards the surface's
// acquired image, and finding none there is FATAL -- a panic that cannot unwind, so the process
// aborts with 0xC0000409 ("Error in wgpuTextureRelease: Surface image is already acquired"). A
// failed present has already taken the image and still marks the texture "not presented", so the
// release that always followed it killed the app. The OpenGL backend fails a present whenever its
// window has gone: GraphWorX died that way when a short-lived window (a splitter's drag bar) was
// destroyed with a frame for it in flight. These tests destroy the window between the acquire and
// the present -- the old order aborted the test host itself.
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;
using WgpuInterop.Tests.Harness;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.Tests.Lifecycle
{
    [Collection(GpuCollection.Name)]
    public sealed unsafe class SurfacePresentTests
    {
        private const int W = 64, H = 48;

        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string cls, string? title, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>A window that is never shown, far off every display, so nothing appears on screen.</summary>
        private static IntPtr HiddenWindow()
        {
            IntPtr hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "surface present test",
                                          WS_POPUP, 8000, 0, W, H, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, hwnd);
            return hwnd;
        }

        private static void Configure(WgpuContext ctx, IntPtr surface)
        {
            WGPUSurfaceCapabilities caps;
            Assert.Equal(WGPUStatus.Success, wgpuSurfaceGetCapabilities(surface, ctx.Adapter, &caps));
            WGPUTextureFormat format = caps.formats[0];
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            var config = new WGPUSurfaceConfiguration
            {
                device = ctx.Device,
                format = format,
                usage = WGPUTextureUsage.RenderAttachment,
                width = W,
                height = H,
                alphaMode = WGPUCompositeAlphaMode.Auto,
                presentMode = WGPUPresentMode.Fifo,
            };
            wgpuSurfaceConfigure(surface, &config);
        }

        private static IntPtr Acquire(IntPtr surface)
        {
            WGPUSurfaceTexture st;
            wgpuSurfaceGetCurrentTexture(surface, &st);
            Assert.SkipWhen(st.texture == IntPtr.Zero, $"the surface gave no image to present ({st.status})");
            return st.texture;
        }

        private static WgpuContext Context()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "HWND surfaces are the Windows head");
            Requirements.RequireGpu();
            return WgpuContext.Create();
        }

        [Fact]
        public void PresentAfterTheWindowIsDestroyed_DoesNotAbort_AndTheSurfaceGoesOn()
        {
            using WgpuContext ctx = Context();
            IntPtr hwnd = HiddenWindow();
            IntPtr surface = NativePlatform.CreateWindowSurface(ctx.Instance, hwnd);
            Assert.NotEqual(IntPtr.Zero, surface);
            try
            {
                Configure(ctx, surface);
                IntPtr texture = Acquire(surface);
                int abandonedBefore = AbandonedSurfaceTextures;

                IntPtr surfaceWindow = hwnd;
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
                // The old order -- wgpuSurfacePresent, then wgpuTextureRelease whatever it said --
                // aborted the process right here whenever the present failed.
                WGPUStatus status = PresentAndRelease(surface, texture);

                if (status != WGPUStatus.Success)
                    Assert.Equal(abandonedBefore + 1, AbandonedSurfaceTextures);
                // Configuring the dead window's surface would abort too ("Invalid surface"); every
                // configure asks this first.
                Assert.False(NativePlatform.IsWindowAlive(surfaceWindow));

                // The device is unharmed: a new window's surface presents.
                IntPtr next = HiddenWindow();
                IntPtr nextSurface = NativePlatform.CreateWindowSurface(ctx.Instance, next);
                try
                {
                    Assert.True(NativePlatform.IsWindowAlive(next));
                    Configure(ctx, nextSurface);
                    PresentAndRelease(nextSurface, Acquire(nextSurface));
                }
                finally
                {
                    DestroyWindow(next);
                    wgpuSurfaceRelease(nextSurface);
                }
            }
            finally
            {
                if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
                wgpuSurfaceRelease(surface);
            }
        }

        [Fact]
        public void ManyFrames_ToALiveWindow_ArePresentedAndReleased()
        {
            using WgpuContext ctx = Context();
            IntPtr hwnd = HiddenWindow();
            IntPtr surface = NativePlatform.CreateWindowSurface(ctx.Instance, hwnd);
            try
            {
                Configure(ctx, surface);
                int abandonedBefore = AbandonedSurfaceTextures;
                for (int i = 0; i < 30; i++)
                {
                    IntPtr texture = Acquire(surface);
                    // Acquire, present, release, 30 times: an image left acquired, or a release of
                    // one already gone, would abort on the next acquire.
                    PresentAndRelease(surface, texture);
                }
                // A live window's presents succeed and release their textures; none are abandoned.
                Assert.Equal(abandonedBefore, AbandonedSurfaceTextures);
            }
            finally
            {
                DestroyWindow(hwnd);
                wgpuSurfaceRelease(surface);
            }
        }
    }
}
