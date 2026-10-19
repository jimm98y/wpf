// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Differential tests for compute shaders: one dispatched on the shim and on real Direct3D 11 (WARP)
// with the same inputs, every UAV compared afterwards word for word.
//
// The resources come from the shader's reflection (RDEF): constant buffers filled by type, textures
// and buffers with deterministic contents, each UAV of the shape its binding declares (typed
// textures and buffers, raw, structured, append/consume/counter). A shader whose results depend on
// the order threads run in (an atomic's returned value, an append) is run, but its UAVs are only
// compared where the order cannot show; DxbcCoverageTests lists those.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    internal static unsafe class DxbcComputeParity
    {
        private const uint BufferBytes = 4096;

        /// <summary>A UAV's contents after the dispatch, as 32-bit words, and whether they are floats.</summary>
        internal sealed record Output(string Name, uint[] Words, bool Float);

        /// <summary>Dispatches the shader (2 x 1 x 1 groups) and returns every UAV's words.</summary>
        public static List<Output> Run(IntPtr dev, IntPtr ctx, byte[] blob, Reflection refl, int seed)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var outputs = new List<(string Name, IntPtr Resource, bool Texture, bool Float)>();
            try
            {
                IntPtr cs;
                fixed (byte* p = blob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateComputeShader))(dev, p, (nuint)blob.Length, IntPtr.Zero, &cs), "CreateComputeShader");
                Keep(cs);
                foreach (var cb in refl.CBuffers)
                {
                    if (cb.Slot < 0) continue;
                    byte[] data = Fill(cb, seed);
                    IntPtr buf;
                    fixed (byte* p = data)
                    {
                        var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)data.Length, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.CONSTANT_BUFFER };
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &buf), "CreateBuffer cb");
                    }
                    Keep(buf);
                    ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetConstantBuffers))(ctx, (uint)cb.Slot, 1, &buf);
                }
                foreach (var b in refl.Bindings)
                {
                    switch (b.Kind)
                    {
                        case 3:
                        {
                            var sd = D3D11_SAMPLER_DESC.Default();
                            sd.Filter = 0;
                            IntPtr s;
                            Check(((delegate* unmanaged<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateSamplerState))(dev, &sd, &s), "CreateSamplerState");
                            Keep(s);
                            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetSamplers))(ctx, (uint)b.Slot, 1, &s);
                            break;
                        }
                        case 2:
                        {
                            IntPtr srv = MakeTexture(dev, ctx, b, false, Keep);
                            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShaderResources))(ctx, (uint)b.Slot, 1, &srv);
                            break;
                        }
                        case 5: case 7:              // StructuredBuffer, ByteAddressBuffer
                        {
                            bool structured = b.Kind == 5;
                            uint stride = structured ? (uint)Math.Max(4, b.Samples) : 4;
                            IntPtr buf = Buffer(dev, D3D11_BIND.SHADER_RESOURCE, structured, stride, seed + b.Slot, Keep);
                            var vd = new D3D11_VIEW_DESC { Format = structured ? DXGI_FORMAT.UNKNOWN : DXGI_FORMAT.R32_TYPELESS, ViewDimension = 11, U0 = 0, U1 = BufferBytes / stride, U2 = structured ? 0u : 1u };
                            IntPtr view;
                            Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, buf, &vd, &view), "CreateShaderResourceView buffer");
                            Keep(view);
                            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShaderResources))(ctx, (uint)b.Slot, 1, &view);
                            break;
                        }
                        case 4:                       // typed UAV
                        {
                            bool f = b.ReturnType == 5, sint = b.ReturnType == 3;
                            int comps = ((b.Flags >> 2) & 3) + 1;
                            var fmt = comps == 4 ? (f ? DXGI_FORMAT.R32G32B32A32_FLOAT : sint ? DXGI_FORMAT.R32G32B32A32_SINT : DXGI_FORMAT.R32G32B32A32_UINT)
                                : (f ? DXGI_FORMAT.R32_FLOAT : sint ? DXGI_FORMAT.R32_SINT : DXGI_FORMAT.R32_UINT);
                            IntPtr resource, view;
                            if (b.Dimension == 1)
                            {
                                resource = Buffer(dev, D3D11_BIND.UNORDERED_ACCESS, false, 4, seed + 100 + b.Slot, Keep);
                                uint element = comps == 4 ? 16u : 4u;
                                var vd = new D3D11_VIEW_DESC { Format = fmt, ViewDimension = 1, U0 = 0, U1 = BufferBytes / element };
                                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, resource, &vd, &view), "CreateUnorderedAccessView typed buffer");
                                outputs.Add((b.Name, resource, false, f));
                            }
                            else
                            {
                                resource = UavTexture(dev, b.Dimension, fmt, seed + 200 + b.Slot, Keep);
                                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, resource, null, &view), "CreateUnorderedAccessView texture");
                                outputs.Add((b.Name, resource, true, f));
                            }
                            Keep(view);
                            uint keepCount = uint.MaxValue;
                            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews))(ctx, (uint)b.Slot, 1, &view, &keepCount);
                            break;
                        }
                        case 6: case 8: case 9: case 10: case 11:   // RWStructured, RWByteAddress, Append, Consume, structured with counter
                        {
                            bool structured = b.Kind != 8;
                            uint stride = structured ? (uint)Math.Max(4, b.Samples) : 4;
                            IntPtr buf = Buffer(dev, D3D11_BIND.UNORDERED_ACCESS | D3D11_BIND.SHADER_RESOURCE, structured, stride, seed + 300 + b.Slot, Keep);
                            uint flags = structured ? (b.Kind is 9 or 10 ? 2u : b.Kind == 11 ? 4u : 0u) : 1u;
                            var vd = new D3D11_VIEW_DESC { Format = structured ? DXGI_FORMAT.UNKNOWN : DXGI_FORMAT.R32_TYPELESS, ViewDimension = 1, U0 = 0, U1 = BufferBytes / stride, U2 = flags };
                            IntPtr view;
                            Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, buf, &vd, &view), "CreateUnorderedAccessView buffer");
                            Keep(view);
                            uint count = b.Kind == 10 ? 64u : 0u;     // a consume buffer starts with items in it
                            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews))(ctx, (uint)b.Slot, 1, &view, &count);
                            // a structured buffer of floats (its element type is in the reflection) is compared as floats
                            bool floats = refl.CBuffers.Any(c => c.Name == b.Name && c.Vars.Any(v => v.Type == 3));
                            outputs.Add((b.Name, buf, false, floats));
                            break;
                        }
                    }
                }
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShader))(ctx, cs, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Dispatch))(ctx, 2, 1, 1);
                var result = new List<Output>();
                foreach (var (name, r, tex, f) in outputs) result.Add(new Output(name, ReadWords(dev, ctx, r, tex, Keep), f));
                return result;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
            }
        }

        private static IntPtr Buffer(IntPtr dev, D3D11_BIND bind, bool structured, uint stride, int seed, Func<IntPtr, IntPtr> keep)
        {
            var words = new uint[BufferBytes / 4];
            var rnd = new Random(seed);
            // small integers in the low words, floats elsewhere: both kinds of data a shader may read
            for (int i = 0; i < words.Length; i++) words[i] = i % 3 == 0 ? (uint)rnd.Next(64) : BitConverter.SingleToUInt32Bits(0.1f + (float)rnd.NextDouble());
            IntPtr buf;
            fixed (uint* p = words)
            {
                var bd = new D3D11_BUFFER_DESC
                {
                    // a structured buffer is a whole number of its elements
                    ByteWidth = structured ? BufferBytes / stride * stride : BufferBytes, Usage = D3D11_USAGE.DEFAULT, BindFlags = bind,
                    MiscFlags = structured ? D3D11_RESOURCE_MISC.BUFFER_STRUCTURED : D3D11_RESOURCE_MISC.BUFFER_ALLOW_RAW_VIEWS,
                    StructureByteStride = structured ? stride : 0,
                };
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &buf), "CreateBuffer");
            }
            return keep(buf);
        }

        private const uint T = 16;

        private static IntPtr UavTexture(IntPtr dev, int dimension, DXGI_FORMAT fmt, int seed, Func<IntPtr, IntPtr> keep)
        {
            uint bpe = fmt is DXGI_FORMAT.R32G32B32A32_FLOAT or DXGI_FORMAT.R32G32B32A32_UINT or DXGI_FORMAT.R32G32B32A32_SINT ? 16u : 4u;
            bool f = fmt is DXGI_FORMAT.R32_FLOAT or DXGI_FORMAT.R32G32B32A32_FLOAT;
            uint depth = dimension == 8 ? 4u : 1u, height = dimension == 2 ? 1u : T;
            var data = new uint[T * height * depth * bpe / 4];
            var rnd = new Random(seed);
            for (int i = 0; i < data.Length; i++) data[i] = f ? BitConverter.SingleToUInt32Bits(0.1f + (float)rnd.NextDouble()) : (uint)rnd.Next(64);
            IntPtr tex;
            fixed (uint* p = data)
            {
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p, SysMemPitch = T * bpe, SysMemSlicePitch = T * height * bpe };
                switch (dimension)
                {
                    case 2:
                    {
                        var d = new D3D11_TEXTURE1D_DESC { Width = T, MipLevels = 1, ArraySize = 1, Format = fmt, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS };
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE1D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture1D))(dev, &d, &init, &tex), "CreateTexture1D");
                        break;
                    }
                    case 8:
                    {
                        var d = new D3D11_TEXTURE3D_DESC { Width = T, Height = T, Depth = depth, MipLevels = 1, Format = fmt, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS };
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE3D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture3D))(dev, &d, &init, &tex), "CreateTexture3D");
                        break;
                    }
                    default:
                    {
                        var d = new D3D11_TEXTURE2D_DESC { Width = T, Height = T, MipLevels = 1, ArraySize = 1, Format = fmt, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS };
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, &init, &tex), "CreateTexture2D");
                        break;
                    }
                }
            }
            return keep(tex);
        }

        /// <summary>A buffer's or texture's contents through a staging copy, as words.</summary>
        private static uint[] ReadWords(IntPtr dev, IntPtr ctx, IntPtr resource, bool texture, Func<IntPtr, IntPtr> keep)
        {
            uint dim;
            ((delegate* unmanaged<IntPtr, uint*, void>)Fn(resource, 7))(resource, &dim);   // ID3D11Resource::GetType
            IntPtr staging;
            uint rows = 1, slices = 1, rowBytes;
            if (!texture)
            {
                D3D11_BUFFER_DESC src;
                ((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, void>)Fn(resource, Vtbl.ID3D11Buffer.GetDesc))(resource, &src);
                var bd = new D3D11_BUFFER_DESC { ByteWidth = src.ByteWidth, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, null, &staging), "CreateBuffer staging");
                rowBytes = src.ByteWidth & ~3u;
            }
            else if (dim == 2)
            {
                D3D11_TEXTURE1D_DESC d;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE1D_DESC*, void>)Fn(resource, Vtbl.ID3D11Texture1D.GetDesc))(resource, &d);
                rowBytes = d.Width * Formats.TexelBytes(d.Format);
                d.Usage = D3D11_USAGE.STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE1D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture1D))(dev, &d, null, &staging), "CreateTexture1D staging");
            }
            else if (dim == 4)
            {
                D3D11_TEXTURE3D_DESC d;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE3D_DESC*, void>)Fn(resource, Vtbl.ID3D11Texture3D.GetDesc))(resource, &d);
                rowBytes = d.Width * Formats.TexelBytes(d.Format); rows = d.Height; slices = d.Depth;
                d.Usage = D3D11_USAGE.STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE3D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture3D))(dev, &d, null, &staging), "CreateTexture3D staging");
            }
            else
            {
                D3D11_TEXTURE2D_DESC d;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Fn(resource, Vtbl.ID3D11Texture2D.GetDesc))(resource, &d);
                rowBytes = d.Width * Formats.TexelBytes(d.Format); rows = d.Height;
                d.Usage = D3D11_USAGE.STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, null, &staging), "CreateTexture2D staging");
            }
            keep(staging);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, staging, resource);
            D3D11_MAPPED_SUBRESOURCE m;
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m), "Map");
            var words = new uint[rowBytes / 4 * rows * slices];
            for (uint z = 0; z < slices; z++)
                for (uint y = 0; y < rows; y++)
                    for (uint x = 0; x < rowBytes / 4; x++)
                        words[(z * rows + y) * (rowBytes / 4) + x] = *(uint*)((byte*)m.pData + z * m.DepthPitch + y * m.RowPitch + x * 4);
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
            return words;
        }

        /// <summary>The words that differ (bits, or for float data beyond a relative 1e-4), and the first.</summary>
        public static (int Count, string First) Compare(List<Output> warp, List<Output> shim)
        {
            int bad = 0; string first = "";
            for (int k = 0; k < Math.Min(warp.Count, shim.Count); k++)
            {
                var a = warp[k]; var b = shim[k];
                for (int i = 0; i < Math.Min(a.Words.Length, b.Words.Length); i++)
                {
                    uint x = a.Words[i], y = b.Words[i];
                    if (x == y) continue;
                    float fx = BitConverter.UInt32BitsToSingle(x), fy = BitConverter.UInt32BitsToSingle(y);
                    // D3D allows its transcendentals a few ulps (sqrt, rsq, exp, log): 1e-5 relative
                    bool close = float.IsFinite(fx) && float.IsFinite(fy) && Math.Abs(fx - fy) <= Math.Max(1e-6f, 1e-5f * Math.Max(Math.Abs(fx), Math.Abs(fy)));
                    if (a.Float && close) continue;
                    if (bad++ == 0) first = $"{a.Name}[{i}]: shim 0x{y:x8} ({fy}), WARP 0x{x:x8} ({fx})";
                }
            }
            return (bad, first);
        }
    }
}
