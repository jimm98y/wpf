// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Texture uploads through the shim read back as written: initial data and UpdateSubresource, every
// mip, row strides that are not multiples of 256 (48x48 RGBA, 33x7 R8, BC1 blocks, half floats) and
// boxes at odd offsets -- many times over, since a lost upload shows only now and then.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DTextureUploadTests
    {
        private static readonly (DXGI_FORMAT Format, uint W, uint H, uint Bpe, uint Block)[] s_cases =
        {
            (DXGI_FORMAT.R8G8B8A8_UNORM, 48, 48, 4, 1),
            (DXGI_FORMAT.R8_UNORM, 33, 7, 1, 1),
            (DXGI_FORMAT.R16G16B16A16_FLOAT, 13, 5, 8, 1),
            (DXGI_FORMAT.B8G8R8A8_UNORM, 65, 3, 4, 1),
        };

        [Fact]
        public void Uploads_ReadBackAsWritten()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var failures = new List<string>();
            try
            {
                for (int round = 0; round < 12; round++)
                    foreach (var c in s_cases)
                    {
                        string? f = Round(dev, ctx, c.Format, c.W, c.H, c.Bpe, c.Block, round);
                        if (f != null) failures.Add($"round {round}: {f}");
                    }
            }
            finally { Release(ctx); Release(dev); }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.GetRange(0, Math.Min(20, failures.Count))));
        }

        /// <summary>The same uploads after a compute shader wrote a buffer that was then copied: on GL
        /// that leaves the buffer bound for unpacking (D3DContext.ResetGlUnpackBinding), and without
        /// the reset every texture uploaded after it read back as zero.</summary>
        [Fact]
        public void Uploads_AfterAComputeWriteAndItsCopy_ReadBackAsWritten()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the HLSL compiler exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] cs = Compile("RWByteAddressBuffer u : register(u0);\n[numthreads(64, 1, 1)] void main(uint3 g : SV_DispatchThreadID) { u.Store(g.x * 4, g.x * 3 + 1); }", "cs_5_0", out string? err);
            Assert.True(cs.Length > 0, err);
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var failures = new List<string>();
            try
            {
                IntPtr shader, buf, uav, st;
                fixed (byte* p = cs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateComputeShader))(dev, p, (nuint)cs.Length, IntPtr.Zero, &shader), "cs");
                Keep(shader);
                var bd = new D3D11_BUFFER_DESC { ByteWidth = 256, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS, MiscFlags = D3D11_RESOURCE_MISC.BUFFER_ALLOW_RAW_VIEWS };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, null, &buf), "buffer");
                Keep(buf);
                var vd = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_TYPELESS, ViewDimension = 1, U0 = 0, U1 = 64, U2 = 1 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, buf, &vd, &uav), "uav");
                Keep(uav);
                var sd = new D3D11_BUFFER_DESC { ByteWidth = 256, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &sd, null, &st), "staging");
                Keep(st);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShader))(ctx, shader, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews))(ctx, 0, 1, &uav, null);
                ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Dispatch))(ctx, 1, 1, 1);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, buf);
                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "map");
                uint got = ((uint*)m.pData)[5];
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
                Assert.Equal(16u, got);
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                for (int round = 0; round < 3; round++)
                    foreach (var c in s_cases)
                        if (Round(dev, ctx, c.Format, c.W, c.H, c.Bpe, c.Block, round) is { } f) failures.Add($"round {round}: {f}");
            }
            finally
            {
                foreach (IntPtr p in made) Release(p);
                Release(ctx); Release(dev);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.GetRange(0, Math.Min(20, failures.Count))));
        }

        private const string LoadVs = "float4 main(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * float2(2, -2) + float2(-1, 1), 0.5, 1); }";
        private const string LoadPs = "Texture2D<float4> t : register(t0);\nfloat4 main(float4 p : SV_Position) : SV_Target { int2 q = int2(p.xy); return q.y < 16 ? t.Load(int3(q.x % 20, q.y % 12, 0)) : t.Load(int3(q.x % 10, q.y % 6, 1)) + t.Load(int3(q.x % 5, q.y % 3, 2)) * 0.5; }";

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        /// <summary>BC1 cannot be read back on this backend (GL copies no compressed texels to a
        /// buffer): a pixel shader loads every texel of every mip instead, against WARP's decode.</summary>
        [Fact]
        public void BlockCompressedUploads_LoadAsWarpLoads()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] vs = Compile(LoadVs, "vs_5_0", out _), ps = Compile(LoadPs, "ps_5_0", out string? perr);
            Assert.True(ps.Length > 0, perr);
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                for (int round = 0; round < 6; round++)
                {
                    var warp = DrawBc(wDev, wCtx, vs, ps, round);
                    var shim = DrawBc(sDev, sCtx, vs, ps, round);
                    // BC1's interpolated colours may round a level apart between decoders (the spec allows it)
                    int bad = 0; string first = "";
                    for (int i = 0; i < warp[0].Length; i++)
                        if (Math.Abs(warp[0][i] - shim[0][i]) > 2.5f / 255) { if (bad++ == 0) first = $"component {i}: shim {shim[0][i]}, WARP {warp[0][i]}"; }
                    Assert.True(bad == 0, $"round {round}: {bad} components differ by more than two levels; {first}");
                }
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
        }

        private static List<float[]> DrawBc(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[] psBlob, int seed)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                const uint W0 = 20, H0 = 12, Mips = 3;
                var rnd = new Random(seed);
                var init = new D3D11_SUBRESOURCE_DATA[Mips];
                var pins = new List<GCHandle>();
                for (int m = 0; m < Mips; m++)
                {
                    uint bw = ((W0 >> m) + 3) / 4, bh = ((H0 >> m) + 3) / 4;
                    var bytes = new byte[(bw * 8 + 4) * bh];
                    rnd.NextBytes(bytes);
                    var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    pins.Add(h);
                    init[m] = new D3D11_SUBRESOURCE_DATA { pSysMem = (void*)h.AddrOfPinnedObject(), SysMemPitch = bw * 8 + 4 };
                }
                IntPtr tex, srv, v, p, rtTex, rtv;
                var td = new D3D11_TEXTURE2D_DESC { Width = W0, Height = H0, MipLevels = Mips, ArraySize = 1, Format = DXGI_FORMAT.BC1_UNORM, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE };
                try
                {
                    fixed (D3D11_SUBRESOURCE_DATA* ip = init)
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, ip, &tex), "bc");
                }
                finally { foreach (var h in pins) h.Free(); }
                Keep(tex);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, tex, null, &srv), "srv"); Keep(srv);
                fixed (byte* q = vsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, q, (nuint)vsBlob.Length, IntPtr.Zero, &v), "vs"); Keep(v);
                fixed (byte* q = psBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, q, (nuint)psBlob.Length, IntPtr.Zero, &p), "ps"); Keep(p);
                var rd = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &rd, null, &rtTex), "rt"); Keep(rtTex);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, rtTex, null, &rtv), "rtv"); Keep(rtv);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, p, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShaderResources))(ctx, 0, 1, &srv);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 3, 0);
                return new List<float[]> { ReadTarget(dev, ctx, rtTex, Keep) };
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr q in made) Release(q);
            }
        }

        /// <summary>A texture of three mips from initial data, then a box of mip 0 by UpdateSubresource;
        /// every texel read back. Null when all arrived.</summary>
        private static string? Round(IntPtr dev, IntPtr ctx, DXGI_FORMAT fmt, uint w0, uint h0, uint bpe, uint block, int seed)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                const uint Mips = 3;
                var rnd = new Random(seed * 31 + (int)fmt);
                var levels = new byte[Mips][];
                var pitches = new uint[Mips];
                var init = new D3D11_SUBRESOURCE_DATA[Mips];
                var pins = new System.Runtime.InteropServices.GCHandle[Mips];
                for (int m = 0; m < Mips; m++)
                {
                    uint w = Math.Max(1, w0 >> m), h = Math.Max(1, h0 >> m);
                    uint bw = (w + block - 1) / block, bh = (h + block - 1) / block;
                    pitches[m] = bw * bpe + 3 * (uint)m;            // an app pitch with slack past the row
                    levels[m] = new byte[pitches[m] * bh];
                    rnd.NextBytes(levels[m]);
                    if (fmt == DXGI_FORMAT.R16G16B16A16_FLOAT)       // halves that read back bit for bit (no NaNs)
                        for (int i = 1; i < levels[m].Length; i += 2) levels[m][i] &= 0x3B;
                    pins[m] = System.Runtime.InteropServices.GCHandle.Alloc(levels[m], System.Runtime.InteropServices.GCHandleType.Pinned);
                    init[m] = new D3D11_SUBRESOURCE_DATA { pSysMem = (void*)pins[m].AddrOfPinnedObject(), SysMemPitch = pitches[m] };
                }
                IntPtr tex;
                var td = new D3D11_TEXTURE2D_DESC { Width = w0, Height = h0, MipLevels = Mips, ArraySize = 1, Format = fmt, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE };
                try
                {
                    fixed (D3D11_SUBRESOURCE_DATA* ip = init)
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, ip, &tex), "CreateTexture2D");
                }
                finally { foreach (var p in pins) p.Free(); }
                Keep(tex);

                // a box of mip 0 at an odd place (block-aligned for BC), its own pitch
                uint bx = block == 1 ? 3u : 4u, by = block == 1 ? 1u : 4u, bwid = block == 1 ? Math.Min(17u, w0 - bx) : 8u, bhei = block == 1 ? Math.Min(4u, h0 - by) : 4u;
                uint bRow = (bwid / block) * bpe, bPitch = bRow + 5, bRows = bhei / block;
                var patch = new byte[bPitch * bRows];
                rnd.NextBytes(patch);
                if (fmt == DXGI_FORMAT.R16G16B16A16_FLOAT) for (int i = 1; i < patch.Length; i += 2) patch[i] &= 0x3B;
                var box = new D3D11_BOX { left = bx, top = by, front = 0, right = bx + bwid, bottom = by + bhei, back = 1 };
                fixed (byte* p = patch)
                    ((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_BOX*, void*, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.UpdateSubresource))(ctx, tex, 0, &box, p, bPitch, 0);
                for (uint r = 0; r < bRows; r++)
                    Array.Copy(patch, r * bPitch, levels[0], (by / block + r) * pitches[0] + (bx / block) * bpe, bRow);

                td.Usage = D3D11_USAGE.STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                IntPtr st;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &st), "staging");
                Keep(st);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, tex);
                for (uint m = 0; m < Mips; m++)
                {
                    uint w = Math.Max(1, w0 >> (int)m), h = Math.Max(1, h0 >> (int)m);
                    uint bw = (w + block - 1) / block, bh = (h + block - 1) / block;
                    D3D11_MAPPED_SUBRESOURCE map;
                    Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, m, D3D11_MAP.READ, 0, &map), "Map");
                    try
                    {
                        for (uint y = 0; y < bh; y++)
                            for (uint x = 0; x < bw * bpe; x++)
                            {
                                byte got = ((byte*)map.pData)[y * map.RowPitch + x], want = levels[m][y * pitches[m] + x];
                                if (got != want) return $"{fmt} {w0}x{h0} mip {m}: byte {x} of row {y} is 0x{got:x2}, written 0x{want:x2}";
                            }
                    }
                    finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, m); }
                }
                return null;
            }
            finally { foreach (IntPtr p in made) Release(p); }
        }
    }
}
