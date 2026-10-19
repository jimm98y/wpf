// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's video processor (D3DVideoProcessor.cs) against WARP's: NV12 and P010 frames through
// VideoProcessorBlt into BGRA, over colour spaces (BT.601 / BT.709, limited / full range, limited RGB
// out), at 1:1, scaled down and up, with a source rectangle, a destination rectangle inside the target
// (the background around it) and rotation. The video processor's filtering is the implementation's,
// so the comparison reports the largest and the mean difference per channel and bounds them; at 1:1
// with flat chroma the conversion itself must be within a code.
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
    public sealed unsafe class D3DVideoProcessorTests
    {
        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private static readonly Guid IID_VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333"), IID_VideoContext = new("61F21C45-3C0E-4a74-9CEA-67100D9AD5E4");

        internal sealed class Case
        {
            public string Name = "";
            public bool P010;
            public uint InW = 64, InH = 48, OutW = 64, OutH = 48;
            public uint StreamColor = (1u << 4);        // BT.601 limited
            public uint OutputColor;                    // full-range RGB
            public int[]? Source, Dest, Target;         // rects: left, top, right, bottom
            public uint Rotation;
            public bool FlatChroma;
            /// <summary>The largest difference allowed in any channel, and of the mean (codes of 255).</summary>
            public int Max = 3; public double Mean = 1.0;
        }

        /// <summary>A frame of codes: luma a gradient with detail, chroma a slower pattern (or flat).</summary>
        private static byte[] Frame(Case c, out uint pitch)
        {
            uint bpe = c.P010 ? 2u : 1u;
            pitch = c.InW * bpe;
            var data = new byte[pitch * (c.InH + c.InH / 2)];
            var rnd = new Random(7);
            for (uint y = 0; y < c.InH; y++)
                for (uint x = 0; x < c.InW; x++)
                {
                    int v = (int)(16 + (x * 200 / c.InW) + ((x + y) % 7) * 3) + rnd.Next(4);
                    Put(data, y * pitch + x * bpe, v, c.P010);
                }
            for (uint y = 0; y < c.InH / 2; y++)
                for (uint x = 0; x < c.InW / 2; x++)
                {
                    int cb = c.FlatChroma ? 100 : (int)(64 + y * 128 / (c.InH / 2));
                    int cr = c.FlatChroma ? 170 : (int)(200 - x * 128 / (c.InW / 2));
                    Put(data, (c.InH + y) * pitch + x * 2 * bpe, cb, c.P010);
                    Put(data, (c.InH + y) * pitch + (x * 2 + 1) * bpe, cr, c.P010);
                }
            return data;
        }

        private static void Put(byte[] d, uint at, int code8, bool p010)
        {
            if (!p010) { d[at] = (byte)Math.Clamp(code8, 0, 255); return; }
            ushort v = (ushort)(Math.Clamp(code8 * 4, 0, 1023) << 6);
            d[at] = (byte)v; d[at + 1] = (byte)(v >> 8);
        }

        internal static uint InputBind = 0x200;      // D3D11_BIND_DECODER: what a decoder's frame has (the hardware refuses SHADER_RESOURCE alone)

        private static IntPtr Fn2(IntPtr o, int slot) => (*(IntPtr**)o)[slot];

        /// <summary>The BGRA texels the device's video processor makes of the case's frame.</summary>
        internal static byte[] Run(IntPtr dev, IntPtr ctx, Case c)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                Guid vd = IID_VideoDevice, vc = IID_VideoContext;
                IntPtr video, vctx;
                Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn2(dev, 0))(dev, &vd, &video), "QI ID3D11VideoDevice");
                Keep(video);
                Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn2(ctx, 0))(ctx, &vc, &vctx), "QI ID3D11VideoContext");
                Keep(vctx);

                byte[] frame = Frame(c, out uint pitch);
                IntPtr input, output, staging;
                fixed (byte* f = frame)
                {
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = f, SysMemPitch = pitch };
                    var td = new D3D11_TEXTURE2D_DESC { Width = c.InW, Height = c.InH, MipLevels = 1, ArraySize = 1, Format = (DXGI_FORMAT)(c.P010 ? 104 : 103), SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = (D3D11_BIND)InputBind };
                    Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, &init, &input), "CreateTexture2D NV12");
                }
                Keep(input);
                var od = new D3D11_TEXTURE2D_DESC { Width = c.OutW, Height = c.OutH, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET | D3D11_BIND.SHADER_RESOURCE };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &od, null, &output), "CreateTexture2D out");
                Keep(output);
                // a known colour where nothing is drawn
                IntPtr rtv;
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, output, null, &rtv), "rtv");
                Keep(rtv);
                float* clear = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);

                var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC { InputWidth = c.InW, InputHeight = c.InH, OutputWidth = c.OutW, OutputHeight = c.OutH, InputFrameRateNumerator = 30, InputFrameRateDenominator = 1, OutputFrameRateNumerator = 30, OutputFrameRateDenominator = 1 };
                IntPtr en, proc, iv, ov;
                Check(((delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_CONTENT_DESC*, IntPtr*, int>)Fn2(video, Vtbl.ID3D11VideoDevice.CreateVideoProcessorEnumerator))(video, &content, &en), "CreateVideoProcessorEnumerator");
                Keep(en);
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr*, int>)Fn2(video, Vtbl.ID3D11VideoDevice.CreateVideoProcessor))(video, en, 0, &proc), "CreateVideoProcessor");
                Keep(proc);
                var ivd = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = 1 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC*, IntPtr*, int>)Fn2(video, Vtbl.ID3D11VideoDevice.CreateVideoProcessorInputView))(video, input, en, &ivd, &iv), "CreateVideoProcessorInputView");
                Keep(iv);
                var ovd = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = 1 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC*, IntPtr*, int>)Fn2(video, Vtbl.ID3D11VideoDevice.CreateVideoProcessorOutputView))(video, output, en, &ovd, &ov), "CreateVideoProcessorOutputView");
                Keep(ov);

                uint sc = c.StreamColor, oc = c.OutputColor;
                ((delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetStreamColorSpace))(vctx, proc, 0, &sc);
                ((delegate* unmanaged<IntPtr, IntPtr, uint*, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetOutputColorSpace))(vctx, proc, &oc);
                // no auto processing (WARP's default may filter): the plain conversion
                ((delegate* unmanaged<IntPtr, IntPtr, uint, int, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode))(vctx, proc, 0, 0);
                if (c.Source is { } s) { var r = new D3D11_RECT_V { left = s[0], top = s[1], right = s[2], bottom = s[3] }; ((delegate* unmanaged<IntPtr, IntPtr, uint, int, D3D11_RECT_V*, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetStreamSourceRect))(vctx, proc, 0, 1, &r); }
                if (c.Dest is { } d) { var r = new D3D11_RECT_V { left = d[0], top = d[1], right = d[2], bottom = d[3] }; ((delegate* unmanaged<IntPtr, IntPtr, uint, int, D3D11_RECT_V*, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetStreamDestRect))(vctx, proc, 0, 1, &r); }
                if (c.Target is { } t) { var r = new D3D11_RECT_V { left = t[0], top = t[1], right = t[2], bottom = t[3] }; ((delegate* unmanaged<IntPtr, IntPtr, int, D3D11_RECT_V*, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetOutputTargetRect))(vctx, proc, 1, &r); }
                if (c.Rotation != 0) ((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorSetStreamRotation))(vctx, proc, 0, 1, c.Rotation);
                var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = 1, pInputSurface = iv };
                Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, uint, D3D11_VIDEO_PROCESSOR_STREAM*, int>)Fn2(vctx, Vtbl.ID3D11VideoContext.VideoProcessorBlt))(vctx, proc, ov, 0, 1, &stream), "VideoProcessorBlt");

                od.Usage = D3D11_USAGE.STAGING; od.BindFlags = 0; od.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &od, null, &staging), "staging");
                Keep(staging);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, staging, output);
                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m), "Map");
                var px = new byte[c.OutW * c.OutH * 4];
                for (uint y = 0; y < c.OutH; y++) new ReadOnlySpan<byte>((byte*)m.pData + y * m.RowPitch, (int)c.OutW * 4).CopyTo(px.AsSpan((int)(y * c.OutW * 4)));
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
                return px;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                for (int i = made.Count - 1; i >= 0; i--) Release(made[i]);
            }
        }

        internal static IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "NV12 BT.601 limited 1:1, flat chroma", FlatChroma = true, Max = 1, Mean = 0.6 };
            yield return new Case { Name = "NV12 BT.709 limited 1:1, flat chroma", StreamColor = 4 | (1u << 4), FlatChroma = true, Max = 1, Mean = 0.6 };
            yield return new Case { Name = "NV12 BT.709 full 1:1, flat chroma", StreamColor = 4 | (2u << 4), FlatChroma = true, Max = 1, Mean = 0.6 };
            // the output's RGB_Range bit (limited, 16-235): this machine's hardware ignores it and writes full range; the
            // shim follows the documented meaning. Reported, bounded loosely
            yield return new Case { Name = "NV12 BT.601 limited in, limited RGB out (the hardware writes full range)", StreamColor = 1u << 4, OutputColor = 2, FlatChroma = true, Max = 20, Mean = 7.5 };
            // at 1:1 the hardware's chroma upsampling is not the bilinear one it scales with (which the shim matches to a
            // code): a code or two at chroma edges
            yield return new Case { Name = "NV12 BT.601 limited 1:1", Max = 2, Mean = 0.6 };
            yield return new Case { Name = "NV12 BT.709 limited 1:1", StreamColor = 4 | (1u << 4), Max = 2, Mean = 0.6 };
            yield return new Case { Name = "NV12 scaled down 2x", OutW = 32, OutH = 24, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 scaled up 1.5x", OutW = 96, OutH = 72, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 a source rect", Source = new[] { 8, 4, 40, 36 }, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 dest rect inside the target (background around)", Dest = new[] { 8, 6, 56, 42 }, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 target rect", Target = new[] { 4, 4, 60, 44 }, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 rotated 90", OutW = 48, OutH = 64, Rotation = 1, Max = 1, Mean = 0.1 };
            yield return new Case { Name = "NV12 rotated 180", Rotation = 2, Max = 2, Mean = 0.6 };
            yield return new Case { Name = "P010 BT.709 limited 1:1, flat chroma", P010 = true, StreamColor = 4 | (1u << 4), FlatChroma = true, Max = 1, Mean = 0.6 };
            yield return new Case { Name = "P010 BT.709 limited 1:1", P010 = true, StreamColor = 4 | (1u << 4), Max = 2, Mean = 0.6 };
        }

        [Fact]
        public void VideoProcessorBlt_ConvertsAsTheHardware()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            // the reference: the hardware's video processor (with video support), else WARP's
            string oracle = "hardware";
            if (RealCreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, &want, 1, 7, &wDev, &level, &wCtx) < 0)
            {
                oracle = "WARP";
                Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            }
            Console.WriteLine("the oracle: " + oracle);
            if (Environment.GetEnvironmentVariable("VP_BIND") is { } vb) InputBind = Convert.ToUInt32(vb, 16);
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            var report = new List<string>();
            var failures = new List<string>();
            int compared = 0;
            try
            {
                foreach (var c in Cases())
                {
                    byte[] w, s;
                    try { w = Run(wDev, wCtx, c); }
                    catch (Exception e) { report.Add($"{c.Name}: WARP: {e.Message}"); continue; }
                    s = Run(sDev, sCtx, c);
                    compared++;
                    int max = 0; long sum = 0; int at = -1;
                    for (int i = 0; i < w.Length; i++)
                    {
                        if ((i & 3) == 3) continue;              // alpha
                        int d = Math.Abs(w[i] - s[i]);
                        sum += d;
                        if (d > max) { max = d; at = i; }
                    }
                    double mean = sum / (w.Length * 0.75);
                    if (Environment.GetEnvironmentVariable("VP_DUMP") == "1")
                    {
                        var sg = new double[3];
                        for (int i = 0; i < w.Length; i++) if ((i & 3) != 3) sg[i & 3] += s[i] - w[i];
                        Console.WriteLine($"  {c.Name}: signed mean B {sg[0] / (w.Length / 4):F2} G {sg[1] / (w.Length / 4):F2} R {sg[2] / (w.Length / 4):F2}; px0 shim {s[2]},{s[1]},{s[0]} hw {w[2]},{w[1]},{w[0]}");
                    }
                    string line = $"{c.Name}: max {max}, mean {mean:F3}" + (at >= 0 ? $" (worst at pixel ({at / 4 % c.OutW}, {at / 4 / c.OutW}) channel {"BGR"[at & 3]}: shim {s[at]}, WARP {w[at]})" : "");
                    report.Add(line);
                    if (max > c.Max || mean > c.Mean) failures.Add(line);
                }
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
            Console.WriteLine(string.Join(Environment.NewLine, report));
            Assert.SkipWhen(compared == 0, "the oracle has no video processor for these frames: " + string.Join("; ", report));
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures) + Environment.NewLine + "all:" + Environment.NewLine + string.Join(Environment.NewLine, report));
        }
    }
}
