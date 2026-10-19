// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3DImage on the managed (WebGPU) composition backend (Documentation/d3d11-webgpu-shim.md, section 8).
//
// milcore's CInteropDeviceBitmap is replaced by ManagedInteropBitmap. It keeps the application's
// IDirect3DSurface9 (with a reference) and, on each present, reads the surface and hands its pixels
// to the compositor as the D3DImage resource's bitmap -- what WPF itself does when the surface cannot
// be shared with its own device (the software path). Direct3D 9 is called through the surface's
// vtable: GetDesc, GetDevice, then the device's CreateOffscreenPlainSurface (system memory),
// GetRenderTargetData and the copy's LockRect. No COM interop: the object handed back to D3DImage
// (a SafeMILHandle, AddRef'd and released through slots 1 and 2 like milcore's objects) is a
// three-slot IUnknown vtable of our own.
//
// A surface made by the Direct3D shim (Microsoft.Wpf.Interop.WebGpu.Direct3D) answers the same calls,
// so this one path serves both.
//

using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Composition;
using MS.Internal;

namespace System.Windows.Interop
{
    internal sealed unsafe class ManagedInteropBitmap
    {
        private const int D3DFMT_A8R8G8B8 = 21, D3DFMT_X8R8G8B8 = 22;
        private const int D3DPOOL_SYSTEMMEM = 2;
        private const int D3DLOCK_READONLY = 0x10;
        private const int D3DERR_INVALIDCALL = unchecked((int)0x8876086C);

        // IDirect3DSurface9 / IDirect3DDevice9 slots (d3d9.h)
        private const int SurfaceGetDevice = 3, SurfaceGetDesc = 12, SurfaceLockRect = 13, SurfaceUnlockRect = 14;
        private const int DeviceGetRenderTargetData = 32, DeviceCreateOffscreenPlainSurface = 36;

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DSURFACE_DESC { public int Format, Type; public uint Usage; public int Pool, MultiSampleType; public uint MultiSampleQuality, Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DLOCKED_RECT { public int Pitch; public void* pBits; }

        private IntPtr _surface;           // the application's, AddRef'd
        private IntPtr _copy;              // our system-memory surface of the same size
        public readonly uint Width, Height;
        public readonly int Format;
        public Int32Rect Dirty;
        /// <summary>This back buffer, to the compositor: its frames share one front buffer there.</summary>
        public readonly long Key = System.Threading.Interlocked.Increment(ref s_keys);
        private static long s_keys;
        private GCHandle _self;
        private IntPtr _unknown;           // the IUnknown block handed to D3DImage
        private int _refs = 1;

        private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

        private ManagedInteropBitmap(IntPtr surface, D3DSURFACE_DESC desc)
        {
            _surface = surface;
            Width = desc.Width;
            Height = desc.Height;
            Format = desc.Format;
            _self = GCHandle.Alloc(this);
            var b = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
            b[0] = (IntPtr)s_vtable;
            b[1] = GCHandle.ToIntPtr(_self);
            _unknown = (IntPtr)b;
        }

        private static readonly IntPtr* s_vtable = BuildVtable();

        private static IntPtr* BuildVtable()
        {
            var vt = (IntPtr*)NativeMemory.AllocZeroed((nuint)(3 * sizeof(IntPtr)));
            vt[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            vt[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRef;
            vt[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Release;
            return vt;
        }

        public static ManagedInteropBitmap? From(SafeHandle h) => h == null || h.IsInvalid ? null : From(h.DangerousGetHandle());

        private static ManagedInteropBitmap? From(IntPtr p) => p == IntPtr.Zero ? null : (ManagedInteropBitmap?)GCHandle.FromIntPtr(((IntPtr*)p)[1]).Target;

        [UnmanagedCallersOnly]
        private static int QueryInterface(IntPtr self, Guid* iid, IntPtr* ppv)
        {
            if (ppv == null) return unchecked((int)0x80004003);
            *ppv = IntPtr.Zero;
            if (iid != null && *iid == new Guid("00000000-0000-0000-C000-000000000046"))
            {
                From(self)?.AddRefCore();
                *ppv = self;
                return 0;
            }
            return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(IntPtr self) => From(self)?.AddRefCore() ?? 0;

        [UnmanagedCallersOnly]
        private static uint Release(IntPtr self) => From(self)?.ReleaseCore() ?? 0;

        private uint AddRefCore() => (uint)System.Threading.Interlocked.Increment(ref _refs);

        private uint ReleaseCore()
        {
            int n = System.Threading.Interlocked.Decrement(ref _refs);
            if (n != 0) return (uint)Math.Max(0, n);
            Detach();
            NativeMemory.Free((void*)_unknown);
            _unknown = IntPtr.Zero;
            if (_self.IsAllocated) _self.Free();
            return 0;
        }

        /// <summary>InteropDeviceBitmap_Create: the surface must be a lockable-format render target.</summary>
        public static int Create(IntPtr surface, out SafeMILHandle? bitmap, out uint width, out uint height)
        {
            bitmap = null; width = height = 0;
            if (surface == IntPtr.Zero) return D3DERR_INVALIDCALL;
            D3DSURFACE_DESC desc;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3DSURFACE_DESC*, int>)Slot(surface, SurfaceGetDesc))(surface, &desc);
            if (hr < 0) return hr;
            if (desc.Format != D3DFMT_A8R8G8B8 && desc.Format != D3DFMT_X8R8G8B8) return D3DERR_INVALIDCALL;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(surface, 1))(surface);
            var b = new ManagedInteropBitmap(surface, desc);
            bitmap = new SafeMILHandle(b._unknown);
            width = desc.Width;
            height = desc.Height;
            return 0;
        }

        /// <summary>InteropDeviceBitmap_Detach: let go of the application's surface now.</summary>
        public void Detach()
        {
            lock (this)
            {
                if (_copy != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(_copy, 2))(_copy); _copy = IntPtr.Zero; }
                if (_surface != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(_surface, 2))(_surface); _surface = IntPtr.Zero; }
            }
        }

        public void AddDirtyRect(int x, int y, int w, int h)
        {
            var r = new Int32Rect(x, y, w, h);
            if (Dirty.IsEmpty || Dirty.Width == 0 || Dirty.Height == 0) { Dirty = r; return; }
            int l = Math.Min(Dirty.X, x), t = Math.Min(Dirty.Y, y);
            int rr = Math.Max(Dirty.X + Dirty.Width, x + w), b = Math.Max(Dirty.Y + Dirty.Height, y + h);
            Dirty = new Int32Rect(l, t, rr - l, b - t);
        }

        /// <summary>Frames read back through the CPU (GetRenderTargetData and LockRect), for tests: the
        /// zero-copy path reads none.</summary>
        internal static int Readbacks;

        /// <summary>The surface to the compositor without its pixels, under this bitmap's lock (the app
        /// cannot let go of it meanwhile): true when the compositor took it.</summary>
        public bool SendSurface(DUCE.Channel channel, DUCE.ResourceHandle handle, Int32Rect dirty)
        {
            lock (this)
            {
                if (_surface == IntPtr.Zero) return false;
                return channel.SendCommandSurface(handle, Key, _surface, (int)Width, (int)Height, Format != D3DFMT_X8R8G8B8, dirty.X, dirty.Y, dirty.Width, dirty.Height);
            }
        }

        /// <summary>The surface's pixels now: straight (non-premultiplied) BGRA32, top-down, Width*4 per
        /// row, or null when the surface cannot be read.</summary>
        public byte[]? ReadPixels() => ReadPixels(new Int32Rect(0, 0, (int)Width, (int)Height));

        /// <summary>The rectangle <paramref name="r"/> of the surface's pixels: straight BGRA32, top-down,
        /// r.Width*4 per row, or null when the surface cannot be read.</summary>
        public byte[]? ReadPixels(Int32Rect r)
        {
            lock (this)
            {
                if (_surface == IntPtr.Zero) return null;
                IntPtr device;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_surface, SurfaceGetDevice))(_surface, &device);
                if (hr < 0 || device == IntPtr.Zero) return null;
                try
                {
                    if (_copy == IntPtr.Zero)
                    {
                        IntPtr copy;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)Slot(device, DeviceCreateOffscreenPlainSurface))(
                            device, Width, Height, Format, D3DPOOL_SYSTEMMEM, &copy, null);
                        if (hr < 0) return null;
                        _copy = copy;
                    }
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(device, DeviceGetRenderTargetData))(device, _surface, _copy);
                    if (hr < 0) return null;
                    System.Threading.Interlocked.Increment(ref Readbacks);
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(device, 2))(device);
                }
                D3DLOCKED_RECT locked;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3DLOCKED_RECT*, void*, uint, int>)Slot(_copy, SurfaceLockRect))(_copy, &locked, null, D3DLOCK_READONLY);
                if (hr < 0) return null;
                var px = new byte[r.Width * r.Height * 4];
                try
                {
                    bool opaque = Format == D3DFMT_X8R8G8B8;
                    fixed (byte* dst = px)
                    {
                        for (int y = 0; y < r.Height; y++)
                        {
                            uint* s = (uint*)((byte*)locked.pBits + (r.Y + y) * locked.Pitch) + r.X;
                            uint* d = (uint*)(dst + y * r.Width * 4);
                            for (int x = 0; x < r.Width; x++)
                            {
                                uint c = s[x];
                                uint a = opaque ? 255u : c >> 24;
                                if (a == 255) { d[x] = c | 0xFF000000; continue; }
                                if (a == 0) { d[x] = 0; continue; }
                                // D3DImage content is premultiplied; the compositor's bitmaps are straight
                                uint cr = Math.Min(255u, ((c >> 16) & 0xFF) * 255 / a), cg = Math.Min(255u, ((c >> 8) & 0xFF) * 255 / a), cb = Math.Min(255u, (c & 0xFF) * 255 / a);
                                d[x] = (a << 24) | (cr << 16) | (cg << 8) | cb;
                            }
                        }
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_copy, SurfaceUnlockRect))(_copy);
                }
                return px;
            }
        }

        /// <summary>The pixels as a frozen BitmapSource (CopyBackBuffer).</summary>
        public BitmapSource? ToBitmapSource(double dpiX, double dpiY)
        {
            byte[]? px = ReadPixels();
            if (px == null) return null;
            var bmp = BitmapSource.Create((int)Width, (int)Height, dpiX, dpiY, PixelFormats.Bgra32, null, px, (int)Width * 4);
            bmp.Freeze();
            return bmp;
        }
    }
}
