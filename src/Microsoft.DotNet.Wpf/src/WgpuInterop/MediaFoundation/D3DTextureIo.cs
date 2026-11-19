// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A texture's bytes, read and written through the shim's own Direct3D 11 -- its device's immediate
// context, as an application would: a staging copy mapped for reading, UpdateSubresource for writing
// (of a planar texture, both planes: D3D's layout, D3DTexture2D.PlaneRows).
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static unsafe class D3DTextureIo
    {
        private static (IntPtr Device, IntPtr Context) DeviceOf(IntPtr texture)
        {
            IntPtr dev, ctx;
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)Com.Fn(texture, Vtbl.ID3D11DeviceChild.GetDevice))(texture, &dev);
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)Com.Fn(dev, Vtbl.ID3D11Device.GetImmediateContext))(dev, &ctx);
            return (dev, ctx);
        }

        /// <summary><paramref name="rows"/> rows of <paramref name="pitch"/> bytes of the subresource into <paramref name="dst"/>.</summary>
        public static void Read(IntPtr texture, uint subresource, byte* dst, int pitch, int rows)
        {
            var (dev, ctx) = DeviceOf(texture);
            IntPtr staging = IntPtr.Zero;
            try
            {
                D3D11_TEXTURE2D_DESC d;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Com.Fn(texture, Vtbl.ID3D11Texture2D.GetDesc))(texture, &d);
                uint mip = subresource % Math.Max(1, d.MipLevels);
                d.Width = Math.Max(1, d.Width >> (int)mip); d.Height = Math.Max(1, d.Height >> (int)mip);
                d.MipLevels = 1; d.ArraySize = 1; d.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 };
                d.Usage = D3D11_USAGE.STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS.READ; d.MiscFlags = 0;
                int hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Com.Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, null, &staging);
                if (hr < 0) throw new InvalidOperationException($"staging texture: 0x{hr:x8}");
                ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)Com.Fn(ctx, Vtbl.ID3D11DeviceContext.CopySubresourceRegion))(ctx, staging, 0, 0, 0, 0, texture, subresource, null);
                D3D11_MAPPED_SUBRESOURCE m;
                hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Com.Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m);
                if (hr < 0) throw new InvalidOperationException($"Map: 0x{hr:x8}");
                for (int r = 0; r < rows; r++) Buffer.MemoryCopy((byte*)m.pData + r * m.RowPitch, dst + r * pitch, pitch, Math.Min(pitch, (int)m.RowPitch));
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Com.Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
            }
            finally
            {
                Com.Release(staging);
                Com.Release(ctx);
                Com.Release(dev);
            }
        }

        public static void Write(IntPtr texture, uint subresource, byte* src, int pitch, int rows)
        {
            var (dev, ctx) = DeviceOf(texture);
            try
            {
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Com.Fn(ctx, Vtbl.ID3D11DeviceContext.UpdateSubresource))(ctx, texture, subresource, null, src, (uint)pitch, (uint)(pitch * rows));
            }
            finally
            {
                Com.Release(ctx);
                Com.Release(dev);
            }
        }

        /// <summary>
        /// The RGB of an NV12 texture of the shim's, on its device's GPU (D3DContext.Nv12ToBgra): a B8G8R8A8 (or B8G8R8X8)
        /// texture of the same size, as a media buffer, AddRef'd once; zero where the texture is not one of the shim's NV12.
        /// </summary>
        public static IntPtr ConvertNv12(MFDxgiBuffer nv12, int w, int h, bool alpha, bool bt709)
        {
            if (ComObject.From<D3DTexture2D>(nv12.Texture) is not { } src || src.Desc.Format != (DXGI_FORMAT)103 || src.Gpu == IntPtr.Zero) return IntPtr.Zero;
            var (dev, ctx) = DeviceOf(nv12.Texture);
            IntPtr tex = IntPtr.Zero;
            try
            {
                if (ComObject.From<D3DContext>(ctx) is not { } context) return IntPtr.Zero;
                var d = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = alpha ? (DXGI_FORMAT)87 : (DXGI_FORMAT)88,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = (D3D11_BIND)(0x8 | 0x20),     // shader resource, render target
                };
                int hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Com.Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, null, &tex);
                if (hr < 0 || ComObject.From<D3DTexture2D>(tex) is not { } dst) return IntPtr.Zero;
                uint slice = nv12.Subresource / Math.Max(1, src.Desc.MipLevels);
                context.Nv12ToBgra(src, slice, dst, (uint)h, bt709, alpha ? (byte)255 : (byte)0);
                return new MFDxgiBuffer(tex, 0).Pointer;
            }
            finally
            {
                Com.Release(tex);
                Com.Release(ctx);
                Com.Release(dev);
            }
        }

        /// <summary>
        /// A B8G8R8A8 (or B8G8R8X8) texture of the device <paramref name="like"/> is of, holding the pixels; as a media
        /// buffer, AddRef'd once.
        /// </summary>
        public static IntPtr CreateBgraBuffer(MFDxgiBuffer like, int w, int h, byte[] pixels, bool alpha)
        {
            var (dev, ctx) = DeviceOf(like.Texture);
            IntPtr tex = IntPtr.Zero;
            try
            {
                var d = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = alpha ? (DXGI_FORMAT)87 : (DXGI_FORMAT)88,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = (D3D11_BIND)0x8,       // shader resource
                };
                fixed (byte* p = pixels)
                {
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p, SysMemPitch = (uint)w * 4 };
                    int hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Com.Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, &init, &tex);
                    if (hr < 0) throw new InvalidOperationException($"RGB texture: 0x{hr:x8}");
                }
                return new MFDxgiBuffer(tex, 0).Pointer;
            }
            finally
            {
                Com.Release(tex);
                Com.Release(ctx);
                Com.Release(dev);
            }
        }
    }
}
