// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation's video renderer of a WPF window's handle: the frame over the window's composited root
// (EmbeddedContent.Compose), letterboxed in its target, and in that window's only.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.Tests.Compositing
{
    public unsafe class VideoSurfaceComposeTests
    {
        [Fact]
        public void AChildWindowsVideo_IsComposedWhereTheChildIsInItsWindow()
        {
            IntPtr window = (IntPtr)0x7E5901, child = (IntPtr)0x7E5902;
            object? had = AppContext.GetData("WpfWebGpu.Wpf.HostedPlacement");
            // the child at (10, 20) of the window's 200x100 client, 100x50 itself
            AppContext.SetData("WpfWebGpu.Wpf.HostedPlacement", (Func<IntPtr, long[]?>)(h => h == child ? new long[] { (long)window, 10, 20, 100, 50, 200, 100 } : null));
            var root = new SceneVisual();
            try
            {
                VideoSurfaces.Present(child, new byte[64 * 48 * 4], 64, 48, 2f);
                var composed = EmbeddedContent.Compose(root, window, 400, 200);         // the target at twice the window's units
                var placed = composed.Children[1];
                Assert.Equal(new System.Numerics.Vector2(20, 40), placed.Offset);
                Assert.Equal(System.Numerics.Matrix3x2.CreateScale(2, 2), placed.Transform);
                Assert.Same(root, EmbeddedContent.Compose(root, (IntPtr)0x7E5903, 400, 200));
            }
            finally { VideoSurfaces.Remove(child); AppContext.SetData("WpfWebGpu.Wpf.HostedPlacement", had); }
        }

        /// <summary>The reader's conversion of a pixel (8-bit fixed point; limited range BT.601/709, full range of each).</summary>
        private static (int R, int G, int B) Reference(int y, int u, int v, bool bt709, bool full)
        {
            int d = u - 128, e = v - 128;
            int c = full ? 256 * y : 298 * (y - 16);
            int rv = 409, gu = 100, gv = 208, bu = 516;
            if (bt709) { rv = 459; gu = 55; gv = 136; bu = 541; }
            if (full) { rv = 359; gu = 88; gv = 183; bu = 454; if (bt709) { rv = 403; gu = 48; gv = 120; bu = 475; } }
            static int B(int x) => Math.Clamp(x, 0, 255);
            return (B((c + rv * e + 128) >> 8), B((c - gu * d - gv * e + 128) >> 8), B((c + bu * d + 128) >> 8));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void AnNv12Frame_IsConvertedOnTheGpuAsTheReaderConvertsIt(bool bt709, bool full)
        {
            const int w = 34, h = 22;
            var hwnd = (IntPtr)(0x7E5800 + (bt709 ? 1 : 0) + (full ? 2 : 0));
            var nv12 = new byte[w * h + w * (h / 2)];
            var rnd = new Random(7);
            rnd.NextBytes(nv12);
            try
            {
                VideoSurfaces.PresentNv12(hwnd, nv12, w, h, 0, bt709, full);
                var img = VideoSurfaces.Snapshot().Find(s => s.Hwnd == hwnd)!.Image!;
                var ctx = ProcessGpu.Acquire();
                try
                {
                    int row = (w * 4 + 255) & ~255;
                    IntPtr buf = ctx.CreateBuffer((ulong)(row * h), WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
                    IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
                    var src = new WGPUTexelCopyTextureInfo { texture = img.Texture, aspect = WGPUTextureAspect.All };
                    var dst = new WGPUTexelCopyBufferInfo { layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)row, rowsPerImage = h }, buffer = buf };
                    var ext = new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = 1 };
                    wgpuCommandEncoderCopyTextureToBuffer(enc, &src, &dst, &ext);
                    IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                    wgpuQueueSubmit(ctx.Queue, 1, &cmd);
                    byte[] px = ctx.MapRead(buf, (ulong)(row * h));
                    wgpuBufferRelease(buf);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int c = w * h + (y / 2) * w + (x / 2) * 2;
                            var (r, g, b) = Reference(nv12[y * w + x], nv12[c], nv12[c + 1], bt709, full);
                            int at = y * row + x * 4;
                            Assert.True((px[at + 2], px[at + 1], px[at]) == (r, g, b), $"({x},{y}): GPU ({px[at + 2]},{px[at + 1]},{px[at]}), expected ({r},{g},{b})");
                        }
                }
                finally { ProcessGpu.Release(ctx); }
            }
            finally { VideoSurfaces.Remove(hwnd); }
        }

        [Fact]
        public void AWindowsVideo_IsComposedOverItsRootOnly()
        {
            var hwnd = (IntPtr)0x7E5701;
            var root = new SceneVisual();
            try
            {
                VideoSurfaces.Present(hwnd, new byte[64 * 48 * 4], 64, 48, 4f / 3);
                Assert.Same(root, EmbeddedContent.Compose(root, (IntPtr)0x7E5702, 800, 300));
                var composed = EmbeddedContent.Compose(root, hwnd, 800, 300);
                Assert.NotSame(root, composed);
                Assert.Same(root, composed.Children[0]);
                var video = composed.Children[1];
                var frame = Assert.Single(video.Children);
                // 4:3 in 800x300: 400x300 centred
                Assert.Equal(new System.Numerics.Vector2(200, 0), frame.Offset);
                Assert.Equal(1, VideoSurfaces.VersionOf(hwnd));
            }
            finally { VideoSurfaces.Remove(hwnd); }
            Assert.Same(root, EmbeddedContent.Compose(root, hwnd, 800, 300));
        }
    }
}
