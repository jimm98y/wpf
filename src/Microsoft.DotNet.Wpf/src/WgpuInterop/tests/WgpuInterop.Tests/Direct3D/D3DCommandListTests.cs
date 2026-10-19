// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Deferred contexts and command lists against WARP: ExecuteCommandList(restore) gives the immediate
// context back its own state -- compute shader and UAVs too -- and FinishCommandList(restore) starts
// the next list from the deferred context's state.
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
    public sealed unsafe class D3DCommandListTests
    {
        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private const string Cs = "RWByteAddressBuffer u : register(u0);\n[numthreads(1, 1, 1)] void main() { uint o; u.InterlockedAdd(0, ADD, o); }\n";

        [Fact]
        public void CommandLists_KeepAndCarryStateAsWarp()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] a = Compile(Cs.Replace("ADD", "1"), "cs_5_0", out string? e1), b = Compile(Cs.Replace("ADD", "100"), "cs_5_0", out string? e2);
            Assert.True(a.Length > 0 && b.Length > 0, e1 + e2);
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = Run(wDev, wCtx, a, b);
                var shim = Run(sDev, sCtx, a, b);
                // A: the immediate dispatch after the restoring Execute (1), and nothing after the one that
                // does not restore. B: the list's dispatch and the next list's, from the carried state (200).
                Assert.Equal((1u, 200u), warp);
                Assert.Equal(warp, shim);
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
        }

        private static (uint, uint) Run(IntPtr dev, IntPtr ctx, byte[] csA, byte[] csB)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                IntPtr Shader(byte[] code)
                {
                    IntPtr s;
                    fixed (byte* p = code) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateComputeShader))(dev, p, (nuint)code.Length, IntPtr.Zero, &s), "CreateComputeShader");
                    return Keep(s);
                }
                (IntPtr Buffer, IntPtr View) Uav()
                {
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.UNORDERED_ACCESS, MiscFlags = D3D11_RESOURCE_MISC.BUFFER_ALLOW_RAW_VIEWS };
                    var zero = new uint[4];
                    IntPtr buf, view;
                    fixed (uint* z = zero)
                    {
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = z };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &buf), "CreateBuffer");
                    }
                    Keep(buf);
                    var vd = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_TYPELESS, ViewDimension = 1, U0 = 0, U1 = 4, U2 = 1 };
                    Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateUnorderedAccessView))(dev, buf, &vd, &view), "CreateUnorderedAccessView");
                    return (buf, Keep(view));
                }
                void Bind(IntPtr c, IntPtr shader, IntPtr view)
                {
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(c, Vtbl.ID3D11DeviceContext.CSSetShader))(c, shader, null, 0);
                    uint keep = uint.MaxValue;
                    ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Fn(c, Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews))(c, 0, 1, &view, &keep);
                }
                void Dispatch(IntPtr c) => ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)Fn(c, Vtbl.ID3D11DeviceContext.Dispatch))(c, 1, 1, 1);

                IntPtr a = Shader(csA), b = Shader(csB);
                var (bufA, uavA) = Uav();
                var (bufB, uavB) = Uav();
                Bind(ctx, a, uavA);

                IntPtr def, l1, l2;
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDeferredContext))(dev, 0, &def), "CreateDeferredContext");
                Keep(def);
                Bind(def, b, uavB);
                Dispatch(def);
                Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Fn(def, Vtbl.ID3D11DeviceContext.FinishCommandList))(def, 1, &l1), "FinishCommandList");
                Keep(l1);
                Dispatch(def);                                   // from the state FinishCommandList kept
                Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Fn(def, Vtbl.ID3D11DeviceContext.FinishCommandList))(def, 0, &l2), "FinishCommandList");
                Keep(l2);

                ((delegate* unmanaged<IntPtr, IntPtr, int, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ExecuteCommandList))(ctx, l1, 1);
                Dispatch(ctx);                                   // A again, restored
                ((delegate* unmanaged<IntPtr, IntPtr, int, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ExecuteCommandList))(ctx, l2, 0);
                Dispatch(ctx);                                   // nothing bound: no effect

                uint Read(IntPtr buf)
                {
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
                    IntPtr st;
                    Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, null, &st), "staging");
                    Keep(st);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, buf);
                    D3D11_MAPPED_SUBRESOURCE m;
                    Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "Map");
                    uint v = *(uint*)m.pData;
                    ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
                    return v;
                }
                return (Read(bufA), Read(bufB));
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                for (int i = made.Count - 1; i >= 0; i--) Release(made[i]);
            }
        }
    }
}
