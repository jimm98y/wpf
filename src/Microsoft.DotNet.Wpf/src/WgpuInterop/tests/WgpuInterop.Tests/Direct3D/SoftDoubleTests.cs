// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The translator's binary64 emulation (DxbcTranslator.Doubles.cs) against .NET's IEEE doubles, bit
// for bit: every operation over operands spread across the exponent range -- normals, subnormals,
// zeros of both signs, infinities, near-ties -- on the GPU, in one compute dispatch per operation.
// A NaN result need only be a NaN.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using WgpuInterop.Tests.Harness;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class SoftDoubleTests
    {
        private const int N = 4096;

        private static double[] Operands(int seed)
        {
            var rnd = new Random(seed);
            var special = new[] { 0.0, -0.0, 1.0, -1.0, 0.5, 2.0, 3.0, 1e-310, -4.9e-324, 2.2250738585072014e-308, double.MaxValue, -double.MaxValue,
                                  double.PositiveInfinity, double.NegativeInfinity, 1.0 + Math.Pow(2, -52), 1.0 - Math.Pow(2, -53), 4294967296.0, 2147483647.75, -2147483648.5, 1e300, 1e-300 };
            var r = new double[N];
            for (int i = 0; i < N; i++)
            {
                switch (rnd.Next(5))
                {
                    case 0: r[i] = special[rnd.Next(special.Length)]; break;
                    case 1: r[i] = BitConverter.Int64BitsToDouble(rnd.NextInt64() & 0x000FFFFFFFFFFFFFL) * (rnd.Next(2) == 0 ? 1 : -1); break;   // subnormal
                    case 2: r[i] = (rnd.NextDouble() - 0.5) * Math.Pow(2, rnd.Next(-40, 40)); break;
                    case 3: r[i] = BitConverter.Int64BitsToDouble(rnd.NextInt64() & 0x7FEFFFFFFFFFFFFFL) * (rnd.Next(2) == 0 ? 1 : -1); break;   // any finite
                    default: r[i] = Math.Round((rnd.NextDouble() - 0.5) * 1e6, rnd.Next(0, 4)); break;
                }
            }
            return r;
        }

        private static uint[] Bits(double[] d) { var u = new uint[d.Length * 2]; for (int i = 0; i < d.Length; i++) { ulong b = (ulong)BitConverter.DoubleToInt64Bits(d[i]); u[2 * i] = (uint)b; u[2 * i + 1] = (uint)(b >> 32); } return u; }

        /// <summary>Runs <paramref name="expr"/> (WGSL over a, b, c: vec2&lt;u32&gt;, and fa/ia: the low
        /// word as f32/i32) per element; the result as two words per element.</summary>
        private static uint[] Run(WgpuContext gpu, D3DDevice device, string expr, bool doubleResult, uint[] a, uint[] b, uint[] c)
        {
            string wgsl = DxbcTranslator.SoftF64Wgsl + $@"
@group(0) @binding(0) var<storage, read> A: array<vec2<u32>>;
@group(0) @binding(1) var<storage, read> B: array<vec2<u32>>;
@group(0) @binding(2) var<storage, read> C: array<vec2<u32>>;
@group(0) @binding(3) var<storage, read_write> R: array<vec2<u32>>;
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) g: vec3<u32>) {{
    let i = g.x;
    if (i >= arrayLength(&R)) {{ return; }}
    let a = A[i]; let b = B[i]; let c = C[i];
    R[i] = {(doubleResult ? expr : $"vec2<u32>({expr}, 0u)")};
}}
";
            IntPtr module = device.Immediate.CreateModule(wgsl, "softf64", out string? err);
            Assert.True(module != IntPtr.Zero, err);
            var entries = stackalloc WGPUBindGroupLayoutEntry[4];
            for (int i = 0; i < 4; i++) entries[i] = new WGPUBindGroupLayoutEntry { binding = (uint)i, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = i == 3 ? WGPUBufferBindingType.Storage : WGPUBufferBindingType.ReadOnlyStorage } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 4, entries = entries };
            IntPtr bgl = wgpuDeviceCreateBindGroupLayout(gpu.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(gpu.Device, &pld);
            byte[] name = System.Text.Encoding.UTF8.GetBytes("main");
            IntPtr pipeline;
            fixed (byte* m = name)
            {
                var cd = new WGPUComputePipelineDescriptor { layout = pl, compute = new WGPUComputeState { module = module, entryPoint = new WGPUStringView { data = m, length = 4 } } };
                pipeline = wgpuDeviceCreateComputePipeline(gpu.Device, &cd);
            }
            IntPtr Up(uint[] data) { var bytes = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); return gpu.CreateBufferMapped(bytes, WGPUBufferUsage.Storage); }
            IntPtr ba = Up(a), bb = Up(b), bc = Up(c);
            ulong rbytes = (ulong)a.Length * 4;
            IntPtr br = gpu.CreateBuffer(rbytes, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            IntPtr read = gpu.CreateBuffer(rbytes, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            var be = stackalloc WGPUBindGroupEntry[4];
            be[0] = new WGPUBindGroupEntry { binding = 0, buffer = ba, size = rbytes };
            be[1] = new WGPUBindGroupEntry { binding = 1, buffer = bb, size = rbytes };
            be[2] = new WGPUBindGroupEntry { binding = 2, buffer = bc, size = rbytes };
            be[3] = new WGPUBindGroupEntry { binding = 3, buffer = br, size = rbytes };
            var bd = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 4, entries = be };
            IntPtr bg = wgpuDeviceCreateBindGroup(gpu.Device, &bd);
            IntPtr enc = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero);
            IntPtr pass = wgpuCommandEncoderBeginComputePass(enc, null);
            wgpuComputePassEncoderSetPipeline(pass, pipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);
            wgpuComputePassEncoderDispatchWorkgroups(pass, (uint)(a.Length / 2 + 63) / 64, 1, 1);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            wgpuCommandEncoderCopyBufferToBuffer(enc, br, 0, read, 0, rbytes);
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(gpu.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            byte[] back = gpu.MapRead(read, rbytes);
            var r = new uint[a.Length];
            Buffer.BlockCopy(back, 0, r, 0, back.Length);
            foreach (IntPtr x in new[] { ba, bb, bc, br, read }) wgpuBufferRelease(x);
            return r;
        }

        private static readonly (string Name, string Expr, bool Double, Func<double, double, double, ulong> Expect)[] s_ops =
        {
            ("add", "dx_dadd(a, b)", true, (x, y, z) => B(x + y)),
            ("sub", "dx_dadd(a, dx_dneg(b))", true, (x, y, z) => B(x - y)),
            ("mul", "dx_dmul(a, b)", true, (x, y, z) => B(x * y)),
            ("div", "dx_ddiv(a, b)", true, (x, y, z) => B(x / y)),
            ("fma", "dx_dfma(a, b, c)", true, (x, y, z) => B(Math.FusedMultiplyAdd(x, y, z))),
            ("min", "dx_dmin(a, b)", true, (x, y, z) => B(double.IsNaN(x) ? y : double.IsNaN(y) ? x : x < y ? x : y)),
            ("max", "dx_dmax(a, b)", true, (x, y, z) => B(double.IsNaN(x) ? y : double.IsNaN(y) ? x : x < y ? y : x)),
            ("eq", "select(0u, 1u, dx_deq(a, b))", false, (x, y, z) => x == y ? 1UL : 0UL),
            ("lt", "select(0u, 1u, dx_dlt(a, b))", false, (x, y, z) => x < y ? 1UL : 0UL),
            ("le", "select(0u, 1u, dx_dle(a, b))", false, (x, y, z) => x <= y ? 1UL : 0UL),
            ("dtof", "dx_dtof(a)", false, (x, y, z) => BitConverter.SingleToUInt32Bits((float)x)),
            ("ftod", "dx_ftod(a.x)", true, (x, y, z) => B((double)BitConverter.UInt32BitsToSingle((uint)BitConverter.DoubleToInt64Bits(x)))),
            ("itod", "dx_itod(a.x)", true, (x, y, z) => B((double)(int)(uint)BitConverter.DoubleToInt64Bits(x))),
            ("utod", "dx_utod(a.x)", true, (x, y, z) => B((double)(uint)BitConverter.DoubleToInt64Bits(x))),
            ("dtoi", "dx_dtoi(a)", false, (x, y, z) => (uint)(double.IsNaN(x) ? 0 : x >= 2147483647.0 ? int.MaxValue : x <= -2147483648.0 ? int.MinValue : (int)Math.Truncate(x))),
            ("dtou", "dx_dtou(a)", false, (x, y, z) => double.IsNaN(x) || x <= 0 ? 0U : x >= 4294967295.0 ? uint.MaxValue : (uint)Math.Truncate(x)),
        };

        private static ulong B(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);

        [System.Runtime.InteropServices.DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private static readonly double[] s_edges =
        {
            0.0, -0.0, 0.5, -0.5, 0.999, -0.999, 1.0, -1.0, 2147483647.0, 2147483647.75, 2147483648.0, -2147483648.0, -2147483648.75, -2147483649.0,
            4294967295.0, 4294967295.5, 4294967296.0, 1e10, -1e10, 1e300, -1e300, 4.9e-324, -4.9e-324, 1e-310, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            3.4028234663852886e38, 3.4028235677973366e38, 1.1754943508222875e-38, 1.4e-45, 7e-46, 1e-50,
            131676.4, -131676.4, 100000.0, 65536.5, 3.0, 12345.678, 1e-5, -1e-5, 0.25, 1048575.9, 16777217.0, 268435456.5,
        };

        private const string ConvertCs = @"
ByteAddressBuffer inp : register(t0);
RWByteAddressBuffer o : register(u0);
[numthreads(64, 1, 1)]
void main(uint3 g : SV_DispatchThreadID)
{
    uint2 v = inp.Load2(g.x * 8);
    double d = asdouble(v.x, v.y);
    o.Store3(g.x * 12, uint3((uint)(int)d, (uint)d, asuint((float)d)));
}";

        /// <summary>dtoi, dtou and dtof at their edges (the ends of the integer ranges, fractions,
        /// float overflow and underflow, NaN and the infinities for dtof) as WARP converts.</summary>
        [Fact]
        public void Conversions_AtTheirEdges_AreWarps()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] cs = DxbcPixelParity.Compile(ConvertCs, "cs_5_0", out string? err);
            Assert.True(cs.Length > 0, err);
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = Convert(wDev, wCtx, cs);
                var shim = Convert(sDev, sCtx, cs);
                var lines = new List<string>();
                // WARP is no oracle for dtoi and dtou outside a narrow band: it converts through a float
                // (dtoi(16777217) is 16777216), gives INT_MIN or INT_MAX for tiny values and for the
                // ends of the range (dtoi(1.1754943508222875e-38) is 0x7FFFFFFF, dtoi(-2^31) too),
                // and even for an ordinary value computed in the shader (dtoi(131676.4...) once came
                // back 0x80000000). D3D's dtoi/dtou truncate toward zero; SoftDoubleTests' IEEE run
                // holds ours to that. Here they are compared where WARP converts sanely: values a
                // float holds exactly, not tiny, inside the type's range.
                bool Sane(double v, bool signed) => (float)v == v && (v == 0 || Math.Abs(v) >= 1e-30)
                    && (signed ? v > -2147483648.0 && v < 2147483647.0 : v > -1.0 && v < 2147483647.0);
                for (int i = 0; i < s_edges.Length; i++)
                    for (int k = 0; k < 3; k++)
                        if (k < 2 && !Sane(s_edges[i], k == 0)) continue;
                        else if (warp[i * 3 + k] != shim[i * 3 + k]) lines.Add($"{new[] { "dtoi", "dtou", "dtof" }[k]}({s_edges[i]:R}): shim 0x{shim[i * 3 + k]:x8}, WARP 0x{warp[i * 3 + k]:x8}");
                Assert.True(lines.Count == 0, string.Join(Environment.NewLine, lines));
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) DxbcPixelParity.Release(p); }
        }

        private static uint[] Convert(IntPtr dev, IntPtr ctx, byte[] csBlob)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                IntPtr cs, inb, srv, outb, uav, st;
                fixed (byte* p = csBlob) DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateComputeShader))(dev, p, (nuint)csBlob.Length, IntPtr.Zero, &cs), "cs");
                Keep(cs);
                var words = Bits(s_edges.Concat(Enumerable.Repeat(0.0, 64 - s_edges.Length)).ToArray());
                fixed (uint* p = words)
                {
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)words.Length * 4, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE, MiscFlags = D3D11_RESOURCE_MISC.BUFFER_ALLOW_RAW_VIEWS };
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                    DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &inb), "in");
                }
                Keep(inb);
                var sv = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_TYPELESS, ViewDimension = 11, U0 = 0, U1 = (uint)words.Length, U2 = 1 };
                DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, inb, &sv, &srv), "srv");
                Keep(srv);
                var od = new D3D11_BUFFER_DESC { ByteWidth = 64 * 12, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS, MiscFlags = D3D11_RESOURCE_MISC.BUFFER_ALLOW_RAW_VIEWS };
                DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &od, null, &outb), "out");
                Keep(outb);
                var uv = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_TYPELESS, ViewDimension = 1, U0 = 0, U1 = 64 * 3, U2 = 1 };
                DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, outb, &uv, &uav), "uav");
                Keep(uav);
                var sd = new D3D11_BUFFER_DESC { ByteWidth = 64 * 12, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
                DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)DxbcPixelParity.Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &sd, null, &st), "st");
                Keep(st);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShader))(ctx, cs, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetShaderResources))(ctx, 0, 1, &srv);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews))(ctx, 0, 1, &uav, null);
                ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.Dispatch))(ctx, 1, 1, 1);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, outb);
                D3D11_MAPPED_SUBRESOURCE m;
                DxbcPixelParity.Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "map");
                var r = new uint[64 * 3];
                new ReadOnlySpan<uint>(m.pData, r.Length).CopyTo(r);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
                return r;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)DxbcPixelParity.Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) DxbcPixelParity.Release(p);
            }
        }

        [Fact]
        public void EveryOperation_IsIeeeExact()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            var gpu = D3DDevice.SharedGpu;
            var device = new D3DDevice(gpu, ownsGpu: false, D3D_FEATURE_LEVEL.L11_0, 0, null);
            double[] x = Operands(1), y = Operands(2), z = Operands(3);
            // near-ties for the rounding paths: y a hair away from x, z cancelling x*y
            for (int i = 0; i < N; i += 7) { y[i] = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x[i]) + (i % 3) - 1); z[i] = -(x[i] * y[i]); }
            uint[] a = Bits(x), b = Bits(y), c = Bits(z);
            var failures = new List<string>();
            try
            {
                foreach (var (name, expr, isDouble, expect) in s_ops)
                {
                    uint[] r = Run(gpu, device, expr, isDouble, a, b, c);
                    int bad = 0; string first = "";
                    for (int i = 0; i < N; i++)
                    {
                        ulong got = r[2 * i] | ((ulong)r[2 * i + 1] << 32);
                        if (!isDouble) got &= 0xFFFFFFFF;
                        ulong want = expect(x[i], y[i], z[i]);
                        bool nan = isDouble ? double.IsNaN(BitConverter.Int64BitsToDouble((long)want)) : name == "dtof" && float.IsNaN(BitConverter.UInt32BitsToSingle((uint)want));
                        bool gotNan = isDouble ? double.IsNaN(BitConverter.Int64BitsToDouble((long)got)) : name == "dtof" && float.IsNaN(BitConverter.UInt32BitsToSingle((uint)got));
                        if (nan ? gotNan : got == want) continue;
                        if (bad++ == 0) first = $"{name}({x[i]:R} [{B(x[i]):x16}], {y[i]:R}, {z[i]:R}): got {got:x16}, want {want:x16}";
                    }
                    if (bad > 0) failures.Add($"{name}: {bad} of {N} wrong; first {first}");
                }
            }
            finally { device.Release(); }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
    }
}
