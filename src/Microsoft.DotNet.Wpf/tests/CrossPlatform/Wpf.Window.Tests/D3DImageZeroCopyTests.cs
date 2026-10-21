// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3DImage over a Direct3D shim surface (Documentation/d3d11-webgpu-shim.md, section 8): every compositor
// and every shim device share the process's GPU device (ProcessGpu.cs), so a frame is a GPU copy -- no
// readback -- and shows exactly what the copy path shows, whichever UI thread composites it and whichever
// thread made the surface; and the app letting go of its surface with a frame in flight is harmless. (The
// surfaces here are made on the test's thread, which composites nothing.)
//
// The shim's Direct3D 9Ex is reached the way an app's d3d9 import reaches it under the launcher: its
// Direct3D9 object, here by reflection (Microsoft.Wpf.Interop.WebGpu's Direct3D types are internal).
//

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wpf.Window.Tests
{
    public sealed unsafe class D3DImageZeroCopyTests
    {
        private static readonly Assembly s_wgpu = typeof(Microsoft.Wpf.Interop.WebGpu.Composition.EmbeddedContent).Assembly;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        private static void Release(IntPtr com) { if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com); }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct LockedRect { public int Pitch; public byte* Bits; }

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

        /// <summary>The shim's IDirect3D9Ex (a reference the caller releases).</summary>
        private static IntPtr ShimD3D9()
        {
            Type t = s_wgpu.GetType("Microsoft.Wpf.Interop.WebGpu.Direct3D.Direct3D9", throwOnError: true)!;
            object d3d = t.GetMethod("Create", All)!.Invoke(null, new object[] { true })!;
            return (IntPtr)d3d.GetType().GetProperty("Pointer", All)!.GetValue(d3d)!;
        }

        private static (IntPtr D3D, IntPtr Device, IntPtr Surface) ShimSurface(uint argb, int format = 21, uint w = 64, uint h = 48)
        {
            IntPtr d3d = ShimD3D9();
            var pp = new D3DPRESENT_PARAMETERS { BackBufferWidth = 1, BackBufferHeight = 1, SwapEffect = 1, hDeviceWindow = GetDesktopWindow(), Windowed = 1, PresentationInterval = 0x80000000 };
            IntPtr device;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, void*, IntPtr*, int>)Slot(d3d, 20))(
                d3d, 0, 1, pp.hDeviceWindow, 0x40 | 0x4 | 0x2, &pp, null, &device);
            Assert.True(hr >= 0, $"CreateDeviceEx 0x{hr:x8}");
            IntPtr surface;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)Slot(device, 28))(device, w, h, format, 0, 0, 1 /* lockable */, &surface, null);
            Assert.True(hr >= 0, $"CreateRenderTarget 0x{hr:x8}");
            Fill(device, surface, argb);
            return (d3d, device, surface);
        }

        private static void Fill(IntPtr device, IntPtr surface, uint argb)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, int>)Slot(device, 35))(device, surface, null, argb);
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

        private static int Readbacks() => (int)typeof(D3DImage).Assembly.GetType("System.Windows.Interop.ManagedInteropBitmap", true)!.GetField("Readbacks", All)!.GetValue(null)!;

        private static object? Sink() => s_wgpu.GetType("Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.WpfCompositionSink", true)!.GetProperty("Current", All)!.GetValue(null);
        private static bool Shares(object sink) => (bool)sink.GetType().GetProperty("SharesShimDevice", All)!.GetValue(sink)!;
        private static int Field(object sink, string name) => (int)sink.GetType().GetField(name, All)!.GetValue(sink)!;

        private static D3DImage Show(IntPtr surface)
        {
            var image = new D3DImage();
            image.Lock();
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
            image.AddDirtyRect(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
            image.Unlock();
            return image;
        }

        private static void SkipUnlessSharing()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the test makes its surface through a desktop window handle");
            object? sink = UiThread.Invoke(() => { Render(new D3DImage(), 1, 1); return Sink(); });
            Assert.SkipWhen(sink == null, "managed composition is not running in this process");
            Assert.SkipUnless(UiThread.Invoke(() => Shares(Sink()!)), "the compositor does not share the process's device with the Direct3D shim");
        }

        /// <summary>A frame of a shim surface: no readback, a zero-copy present, the surface's colour;
        /// translucent, and X8R8G8B8 (whose fourth byte is no alpha).</summary>
        [Fact]
        public void AShimSurface_IsCopiedOnTheGpu_WithNoReadback()
        {
            SkipUnlessSharing();
            foreach (var (argb, format, expect) in new[] { (0xFF2080C0u, 21, 0xFF2080C0u), (0x80402010u, 21, 0x80402010u), (0x12345678u, 22, 0xFF345678u) })
            {
                var (d3d, device, surface) = ShimSurface(argb, format);
                try
                {
                    UiThread.Invoke(() =>
                    {
                        object sink = Sink()!;
                        int readbacks = Readbacks(), zero = Field(sink, "ZeroCopyPresents");
                        uint[] px = Render(Show(surface), 64, 48);
                        Assert.Equal(readbacks, Readbacks());
                        Assert.True(Field(sink, "ZeroCopyPresents") > zero, "the frame did not take the zero-copy path");
                        Assert.Equal(expect, px[24 * 64 + 32]);
                    });
                }
                finally { Release(surface); Release(device); Release(d3d); }
            }
        }

        /// <summary>In a window (the asynchronous channel, whose texture is kept): a second frame through
        /// its dirty rectangle changes that rectangle only -- outside it the first frame stays, though the
        /// surface changed there too -- and neither frame is read back.</summary>
        [Fact]
        public void InAWindow_TheNextFrame_CopiesItsDirtyRectangleOnly()
        {
            SkipUnlessSharing();
            var (d3d, device, surface) = ShimSurface(0xFF2080C0, 21, 64, 48);
            System.Windows.Window? window = null;
            try
            {
                int readbacks = 0;
                D3DImage? image = null;
                window = UiThread.Invoke(() =>
                {
                    readbacks = Readbacks();
                    image = Show(surface);
                    // off screen, not activated: nothing appears on the desktop
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests d3dimage", Left = 8000, Top = 100, Width = 300, Height = 220,
                        WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                        Background = Brushes.White,
                        Content = new System.Windows.Controls.Image { Source = image, Width = 200, Height = 150, Stretch = Stretch.Fill },
                    };
                    w.Show();
                    return w;
                });
                IntPtr hwnd = UiThread.Invoke(() => new WindowInteropHelper(window!).Handle);
                uint corner = 0, centre = 0;
                Assert.True(UiThread.WaitUntil(() => (corner = Sample(hwnd, 0.01, 0.01)) == 0x2080C0 && (centre = Sample(hwnd, 0.5, 0.5)) == 0x2080C0, 5000),
                    $"the first frame never showed (corner 0x{corner:x6}, centre 0x{centre:x6})");

                // all of the surface green; only the top-left 8x8 said to have changed
                UiThread.Invoke(() =>
                {
                    Assert.True(image!.TryLock(new Duration(TimeSpan.FromSeconds(2))));
                    Fill(device, surface, 0xFF00FF00);
                    image.AddDirtyRect(new Int32Rect(0, 0, 8, 8));
                    image.Unlock();
                });
                Assert.True(UiThread.WaitUntil(() => (corner = Sample(hwnd, 0.01, 0.01)) == 0x00FF00, 5000), $"the dirty rectangle never showed (0x{corner:x6})");
                Assert.Equal(0x2080C0u, Sample(hwnd, 0.5, 0.5));
                Assert.Equal(readbacks, UiThread.Invoke(Readbacks));
            }
            finally
            {
                if (window is not null) UiThread.Invoke(() => { try { window.Close(); } catch { } });
                Release(surface); Release(device); Release(d3d);
            }
        }

        /// <summary>The RGB at a point of the 200x150 image (as fractions of it; it is centred in the
        /// client area), by PrintWindow.</summary>
        private static uint Sample(IntPtr hwnd, double fx, double fy)
        {
            GetClientRect(hwnd, out RECT cr);
            int w = cr.Right, h = cr.Bottom;
            if (w <= 0 || h <= 0) return 0;
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int tx = (int)(w / 2 + (fx - 0.5) * 200 * scale), ty = (int)(h / 2 + (fy - 0.5) * 150 * scale);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bm = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(dc, bm);
            try
            {
                PrintWindow(hwnd, dc, 1 /* PW_CLIENTONLY */);
                GdiFlush();
                return ((uint*)bits)[Math.Clamp(ty, 0, h - 1) * w + Math.Clamp(tx, 0, w - 1)] & 0xFFFFFF;
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(bm);
                DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();

        /// <summary>The same surface content, through the zero-copy path and through the copy path (the
        /// pixels read back, as for a real Direct3D 9 surface): the same pixels.</summary>
        [Fact]
        public void ZeroCopy_ShowsWhatTheCopyPathShows()
        {
            SkipUnlessSharing();
            var (d3d, device, surface) = ShimSurface(0x80402010);
            try
            {
                // a gradient of alphas and colours, set through the surface's lock
                const int W = 64, H = 48;
                LockedRect lr;
                int hrL = ((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, void*, uint, int>)Slot(surface, 13))(surface, &lr, null, 0);
                Assert.True(hrL >= 0, $"LockRect 0x{hrL:x8}");
                int pitch = lr.Pitch;
                byte* bits = lr.Bits;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        uint a = (uint)(x * 4), c = (uint)Math.Min(a, (uint)(y * 5));
                        *(uint*)(bits + y * pitch + x * 4) = (a << 24) | (c << 16) | ((c / 2) << 8) | (c / 3);
                    }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(surface, 14))(surface);
                UiThread.Invoke(() =>
                {
                    uint[] zero = Render(Show(surface), W, H);
                    // the copy path, by hand: the pixels as ReadPixels makes them, as a bitmap
                    var managed = typeof(D3DImage).Assembly.GetType("System.Windows.Interop.ManagedInteropBitmap", true)!;
                    var image = Show(surface);
                    object? interop = typeof(D3DImage).GetField("_pInteropDeviceBitmap", All)!.GetValue(image);
                    object bitmap = managed.GetMethod("From", All, new[] { typeof(SafeHandle) })!.Invoke(null, new[] { interop })!;
                    byte[] px = (byte[])managed.GetMethod("ReadPixels", All, Type.EmptyTypes)!.Invoke(bitmap, null)!;
                    var copy = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
                    var dv = new DrawingVisual();
                    using (DrawingContext dc = dv.RenderOpen()) dc.DrawImage(copy, new Rect(0, 0, W, H));
                    var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    var expected = new uint[W * H];
                    rtb.CopyPixels(expected, W * 4, 0);
                    int differ = 0, first = -1;
                    for (int i = 0; i < expected.Length; i++) if (expected[i] != zero[i]) { differ++; if (first < 0) first = i; }
                    Assert.True(differ == 0, $"{differ} pixels differ; first ({first % W}, {first / W}): zero-copy 0x{(first >= 0 ? zero[first] : 0):x8}, copy path 0x{(first >= 0 ? expected[first] : 0):x8}");
                });
            }
            finally { Release(surface); Release(device); Release(d3d); }
        }

        /// <summary>A second UI thread's compositor is on the same GPU device as every other: its D3DImage of
        /// a shim surface made elsewhere (here: on a thread that composites nothing) is a GPU copy too (no
        /// readback), and shows the same. With a device per UI thread it was read back, and a splash screen's
        /// thread made the first device, which DXEngine's frames on the main thread never shared.</summary>
        [Fact]
        public void AnotherUiThread_SharesTheDevice()
        {
            SkipUnlessSharing();
            var (d3d, device, surface) = ShimSurface(0xFF336699);
            Exception? failure = null;
            uint pixel = 0;
            int readbacks = Readbacks();
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        pixel = Render(Show(surface), 64, 48)[24 * 64 + 32];
                    }
                    catch (Exception e) { failure = e; }
                    finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                Assert.True(thread.Join(30000), "the second UI thread did not finish");
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.True(Readbacks() == readbacks, "the second thread's frame was read back rather than copied on the GPU");
                Assert.Equal(0xFF336699u, pixel);
            }
            finally { Release(surface); Release(device); Release(d3d); }
        }

        /// <summary>The app presents, lets go of its surface and device at once, and the frame is drawn
        /// after: WPF's reference keeps the surface, the GPU's copy needs nothing more; then the back
        /// buffer is cleared, and a resize gives the resource a new texture.</summary>
        [Fact]
        public void TheAppLettingGoMidFrame_IsHarmless()
        {
            SkipUnlessSharing();
            var (d3d, device, surface) = ShimSurface(0xFF884422);
            var (d3d2, device2, bigger) = ShimSurface(0xFF224488, 21, 96, 64);
            UiThread.Invoke(() =>
            {
                var image = Show(surface);
                Release(surface); Release(device); Release(d3d);     // WPF holds the surface now
                Assert.Equal(0xFF884422u, Render(image, 64, 48)[24 * 64 + 32]);
                // a resize: another surface, another size
                image.Lock();
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, bigger);
                image.AddDirtyRect(new Int32Rect(0, 0, 96, 64));
                image.Unlock();
                Release(bigger); Release(device2); Release(d3d2);
                Assert.Equal(0xFF224488u, Render(image, 96, 64)[32 * 96 + 48]);
                image.Lock();
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                image.Unlock();
                Render(image, 96, 64);
            });
        }
    }
}
