// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3DImage on the managed composition backend (Documentation/d3d11-webgpu-shim.md, section 8): an
// application's own Direct3D 9Ex surface, set as the back buffer, shows. It used to throw
// DllNotFoundException (wpfgfx_cor3.dll) from SetBackBuffer -- milcore's InteropDeviceBitmap is gone --
// which left GraphWorX's 3D views and SharpMediaFoundationInterop's GPU video control empty.
//
// The surface is made the way those applications make theirs: Direct3DCreate9Ex, CreateDeviceEx and
// a render target, through the vtables.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wpf.Window.Tests
{
    public sealed unsafe class D3DImageTests
    {
        [DllImport("d3d9.dll")]
        private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DPRESENT_PARAMETERS
        {
            public uint BackBufferWidth, BackBufferHeight;
            public int BackBufferFormat;
            public uint BackBufferCount;
            public int MultiSampleType;
            public uint MultiSampleQuality;
            public int SwapEffect;
            public IntPtr hDeviceWindow;
            public int Windowed, EnableAutoDepthStencil, AutoDepthStencilFormat;
            public uint Flags, FullScreen_RefreshRateInHz, PresentationInterval;
        }

        private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        private static void Release(IntPtr com) { if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com); }

        /// <summary>A Direct3D 9Ex device and a 64x48 A8R8G8B8 render target filled with one colour.</summary>
        private static (IntPtr D3D, IntPtr Device, IntPtr Surface) MakeSurface(uint argb, uint w = 64, uint h = 48)
        {
            Assert.True(Direct3DCreate9Ex(32, out IntPtr d3d) >= 0, "Direct3DCreate9Ex");
            var pp = new D3DPRESENT_PARAMETERS { BackBufferWidth = 1, BackBufferHeight = 1, SwapEffect = 1, hDeviceWindow = GetDesktopWindow(), Windowed = 1, PresentationInterval = 0x80000000 };
            IntPtr device;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, void*, IntPtr*, int>)Slot(d3d, 20))(
                d3d, 0, 1 /* HAL */, pp.hDeviceWindow, 0x40 | 0x4 | 0x2, &pp, null, &device);
            if (hr < 0)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, void*, IntPtr*, int>)Slot(d3d, 20))(
                    d3d, 0, 1, pp.hDeviceWindow, 0x20 /* software vertex processing */ | 0x4 | 0x2, &pp, null, &device);
            }
            Assert.True(hr >= 0, $"CreateDeviceEx 0x{hr:x8}");
            IntPtr surface;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)Slot(device, 28 /* CreateRenderTarget */))(
                device, w, h, 21 /* A8R8G8B8 */, 0, 0, 0, &surface, null);
            Assert.True(hr >= 0, $"CreateRenderTarget 0x{hr:x8}");
            Fill(device, surface, argb);
            return (d3d, device, surface);
        }

        private static void Fill(IntPtr device, IntPtr surface, uint argb)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, int>)Slot(device, 35 /* ColorFill */))(device, surface, null, argb);
            Assert.True(hr >= 0, $"ColorFill 0x{hr:x8}");
        }

        private static uint[] Render(D3DImage image, int w, int h)
        {
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new uint[w * h];
            rtb.CopyPixels(px, w * 4, 0);
            return px;
        }

        [Fact]
        public void ADirect3D9SurfaceSetAsTheBackBuffer_Shows()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 9 surfaces exist on Windows only");
            var (d3d, device, surface) = MakeSurface(0xFF2080C0);
            try
            {
                uint[] px = UiThread.Invoke(() =>
                {
                    var image = new D3DImage();
                    image.Lock();
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
                    image.AddDirtyRect(new Int32Rect(0, 0, 64, 48));
                    image.Unlock();
                    Assert.Equal(64, image.PixelWidth);
                    Assert.Equal(48, image.PixelHeight);
                    Assert.True(image.IsFrontBufferAvailable);
                    return Render(image, 64, 48);
                });
                Assert.Equal(0xFF2080C0u, px[24 * 64 + 32]);
                Assert.Equal(0xFF2080C0u, px[0]);
            }
            finally
            {
                Release(surface); Release(device); Release(d3d);
            }
        }

        [Fact]
        public void ANewFrameInTheSameSurface_ShowsAfterItsDirtyRect()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 9 surfaces exist on Windows only");
            var (d3d, device, surface) = MakeSurface(0xFFFF0000);
            try
            {
                UiThread.Invoke(() =>
                {
                    var image = new D3DImage();
                    image.Lock();
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
                    image.AddDirtyRect(new Int32Rect(0, 0, 64, 48));
                    image.Unlock();
                    Assert.Equal(0xFFFF0000u, Render(image, 64, 48)[100]);

                    // the application draws the next frame, then tells WPF -- as a video control does
                    Assert.True(image.TryLock(new Duration(TimeSpan.FromSeconds(2))), "the back buffer stayed locked");
                    Fill(device, surface, 0xFF00FF00);
                    image.AddDirtyRect(new Int32Rect(0, 0, 64, 48));
                    image.Unlock();
                    Assert.Equal(0xFF00FF00u, Render(image, 64, 48)[100]);
                    return 0;
                });
            }
            finally
            {
                Release(surface); Release(device); Release(d3d);
            }
        }

        [Fact]
        public void ClearingTheBackBuffer_LetsGoOfTheSurface()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 9 surfaces exist on Windows only");
            var (d3d, device, surface) = MakeSurface(0xFF0000FF);
            try
            {
                uint refsBefore = AddRefRelease(surface);
                UiThread.Invoke(() =>
                {
                    var image = new D3DImage();
                    image.Lock();
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
                    image.Unlock();
                    Assert.Equal(refsBefore + 1, AddRefRelease(surface));     // WPF holds one
                    image.Lock();
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                    image.Unlock();
                    return 0;
                });
                Assert.Equal(refsBefore, AddRefRelease(surface));               // and gave it back
            }
            finally
            {
                Release(surface); Release(device); Release(d3d);
            }
        }

        /// <summary>The surface's reference count (an AddRef and its Release).</summary>
        private static uint AddRefRelease(IntPtr com)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 1))(com);
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);
        }
    }
}
