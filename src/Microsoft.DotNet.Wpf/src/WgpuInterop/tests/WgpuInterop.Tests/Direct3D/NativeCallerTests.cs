// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's objects as native code sees them (Media Foundation, an app's COM wrappers): QueryInterface
// for every interface a device and its context answer -- ID3D10Multithread was unreachable once, its IID
// read from a static field not yet initialised -- one identity behind them all, a clean E_NOINTERFACE for
// what WebGPU cannot do (ID3D12Device), references taken and let go of from other threads, the
// multithread lock, queries, and D3DCompile answering E_FAIL (there is no compiler in the shim).
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class NativeCallerTests
    {
        private static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        private static uint AddRef(IntPtr o) => ((delegate* unmanaged<IntPtr, uint>)Fn(o, 1))(o);
        private static uint Release(IntPtr o) => ((delegate* unmanaged<IntPtr, uint>)Fn(o, 2))(o);

        private static int Qi(IntPtr o, string iid, out IntPtr p)
        {
            Guid g = new(iid);
            IntPtr q;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(o, 0))(o, &g, &q);
            p = q;
            return hr;
        }

        private const string IUnknown = "00000000-0000-0000-C000-000000000046";

        [Fact]
        public void TheDeviceAndContext_AnswerWhatNativeCallersAskFor()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x820, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            try
            {
                Assert.Equal(0, Qi(dev, IUnknown, out IntPtr unk));
                foreach (string iid in new[]
                {
                    "db6f6ddb-ac77-4e88-8253-819df9bbf140",   // ID3D11Device
                    "54ec77fa-1377-44e6-8c32-88fd5f44c84c",   // IDXGIDevice
                    "9b7e4e00-342c-4106-a19f-4f2704f689f0",   // ID3D10Multithread / ID3D11Multithread
                    "10EC4D5B-975A-4689-B9E4-D0AAC30FE333",   // ID3D11VideoDevice
                })
                {
                    Assert.True(Qi(dev, iid, out IntPtr p) == 0, "device: " + iid);
                    // every interface of the one object leads back to the same IUnknown
                    Assert.Equal(0, Qi(p, IUnknown, out IntPtr u2));
                    Assert.Equal(unk, u2);
                    Release(u2);
                    Release(p);
                }
                Assert.Equal(HResult.E_NOINTERFACE, Qi(dev, "189819f1-1db6-4b57-be54-1821339b85f7", out IntPtr d12));   // ID3D12Device
                Assert.Equal(IntPtr.Zero, d12);
                Assert.True(Qi(ctx, "61F21C45-3C0E-4a74-9CEA-67100D9AD5E4", out IntPtr vctx) == 0, "ID3D11VideoContext");
                Release(vctx);
                Release(unk);

                // ID3D10Multithread: Enter and Leave on two threads, one at a time
                Assert.Equal(0, Qi(dev, "9b7e4e00-342c-4106-a19f-4f2704f689f0", out IntPtr mt));
                ((delegate* unmanaged<IntPtr, int, int>)Fn(mt, 5))(mt, 1);
                Assert.Equal(1, ((delegate* unmanaged<IntPtr, int>)Fn(mt, 6))(mt));
                ((delegate* unmanaged<IntPtr, void>)Fn(mt, 3))(mt);           // Enter
                bool entered = false;
                IntPtr lockOf = mt;
                var other = Task.Run(() =>
                {
                    ((delegate* unmanaged<IntPtr, void>)Fn(lockOf, 3))(lockOf);
                    entered = true;
                    ((delegate* unmanaged<IntPtr, void>)Fn(lockOf, 4))(lockOf);
                });
                Thread.Sleep(200);
                Assert.False(entered);                                          // held by this thread
                ((delegate* unmanaged<IntPtr, void>)Fn(mt, 4))(mt);           // Leave
                Assert.True(other.Wait(5000));
                Assert.True(entered);
                Release(mt);

                // references from other threads, many at once: the count comes back to where it was
                IntPtr device = dev;
                uint before = AddRef(device) - 1;
                Release(device);
                Parallel.For(0, 64, _ => { for (int i = 0; i < 100; i++) { AddRef(device); Release(device); } });
                Assert.Equal(before, AddRef(dev) - 1);
                Release(dev);

                // an event query, answered once the GPU has done what came before it
                var qd = new D3D11_QUERY_DESC { Query = D3DQuery.Event };
                IntPtr query;
                Assert.Equal(0, ((delegate* unmanaged<IntPtr, D3D11_QUERY_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateQuery))(dev, &qd, &query));
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.End))(ctx, query);
                int done = 0, hr = 1;
                for (int i = 0; i < 1000 && hr == 1; i++)
                {
                    hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.GetData))(ctx, query, &done, 4, 0);
                    if (hr == 1) Thread.Sleep(1);
                }
                Assert.Equal(0, hr);
                Assert.Equal(1, done);
                Release(query);
            }
            finally { Release(ctx); Release(dev); }
        }

        /// <summary>D3DCompile on every OS: E_FAIL, no blobs, no compiler loaded (the shim has none yet).</summary>
        [Fact]
        public void D3DCompile_AnswersEFail()
        {
            byte[] src = System.Text.Encoding.ASCII.GetBytes("float4 main() : SV_Target { return 1; }");
            byte[] entry = System.Text.Encoding.ASCII.GetBytes("main\0"), target = System.Text.Encoding.ASCII.GetBytes("ps_4_0\0");
            IntPtr code = (IntPtr)1, errors = (IntPtr)1;
            int hr;
            fixed (byte* s = src)
            fixed (byte* e = entry)
            fixed (byte* t = target)
                hr = Exports.CompilePointer(s, (nuint)src.Length, null, null, null, e, t, 0, 0, &code, &errors);
            Assert.Equal(HResult.E_FAIL, hr);
            Assert.Equal(IntPtr.Zero, code);
            Assert.Equal(IntPtr.Zero, errors);
        }
    }
}
