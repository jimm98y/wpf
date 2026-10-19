// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// queue.writeTexture on this GL backend, in isolation: does its data arrive? Each scenario writes a
// fresh 16x16 RGBA8 texture, copies it to a buffer and reads it back, many times, and counts the
// textures whose texels did not arrive -- alone, with another device alive in the process, with
// another device used in between, with an encoder open across the write, after a compute pass.
//
// Found (WRITETEXTURE_PROBE=1): every scenario delivers all 200 but one. After a compute pass writes
// a storage buffer and that buffer is copied, all 200 are lost, and they stay lost until a
// copyBufferToTexture runs. wgpu-hal's GL queue synchronises a storage buffer bound for a copy
// with a BufferBarrier that binds it to GL_PIXEL_UNPACK_BUFFER and never unbinds it; writeTexture's
// glTexSubImage then reads from that buffer instead of the client memory it passes. The shim resets
// the binding (D3DContext.ResetGlUnpackBinding); D3DTextureUploadTests holds it to that.
// WRITETEXTURE_WATCH=1 names the first test after which the shared device loses writes.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class WriteTextureProbeTests
    {
        private const uint S = 16;

        private static IntPtr MakeTexture(WgpuContext gpu)
        {
            var d = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.CopyDst | WGPUTextureUsage.CopySrc | WGPUTextureUsage.TextureBinding,
                dimension = WGPUTextureDimension._2D, size = new WGPUExtent3D { width = S, height = S, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.RGBA8Unorm, mipLevelCount = 1, sampleCount = 1,
            };
            return wgpuDeviceCreateTexture(gpu.Device, &d);
        }

        private static void Write(WgpuContext gpu, IntPtr tex, uint[] px)
        {
            fixed (uint* p = px)
            {
                var dst = new WGPUTexelCopyTextureInfo { texture = tex, aspect = WGPUTextureAspect.All };
                var layout = new WGPUTexelCopyBufferLayout { bytesPerRow = S * 4, rowsPerImage = S };
                var size = new WGPUExtent3D { width = S, height = S, depthOrArrayLayers = 1 };
                wgpuQueueWriteTexture(gpu.Queue, &dst, p, (nuint)(px.Length * 4), &layout, &size);
            }
        }

        private static void Submit(WgpuContext gpu, IntPtr encoder)
        {
            IntPtr cmd = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
            wgpuCommandEncoderRelease(encoder);
            wgpuQueueSubmit(gpu.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
        }

        /// <summary>The texture's texels, through a copy into a buffer recorded in <paramref name="encoder"/>
        /// (or a fresh one).</summary>
        private static uint[] Read(WgpuContext gpu, IntPtr tex, IntPtr encoder = default)
        {
            IntPtr buf = gpu.CreateBuffer(256 * S, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
            if (encoder == IntPtr.Zero) encoder = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero);
            var src = new WGPUTexelCopyTextureInfo { texture = tex, aspect = WGPUTextureAspect.All };
            var dst = new WGPUTexelCopyBufferInfo { buffer = buf, layout = new WGPUTexelCopyBufferLayout { bytesPerRow = 256, rowsPerImage = S } };
            var size = new WGPUExtent3D { width = S, height = S, depthOrArrayLayers = 1 };
            wgpuCommandEncoderCopyTextureToBuffer(encoder, &src, &dst, &size);
            Submit(gpu, encoder);
            byte[] bytes = gpu.MapRead(buf, 256 * S);
            wgpuBufferRelease(buf);
            var r = new uint[S * S];
            for (int y = 0; y < S; y++) Buffer.BlockCopy(bytes, y * 256, r, y * (int)S * 4, (int)S * 4);
            return r;
        }

        private static int Run(WgpuContext gpu, int rounds, Action<int>? between, bool encoderAcross)
        {
            int lost = 0;
            var rnd = new Random(7);
            for (int i = 0; i < rounds; i++)
            {
                var px = new uint[S * S];
                for (int k = 0; k < px.Length; k++) px[k] = (uint)rnd.Next() | 0x01010101;
                IntPtr encoder = encoderAcross ? wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero) : IntPtr.Zero;
                IntPtr tex = MakeTexture(gpu);
                Write(gpu, tex, px);
                between?.Invoke(i);
                uint[] back = Read(gpu, tex, encoder);
                if (!back.AsSpan().SequenceEqual(px)) lost++;
                wgpuTextureRelease(tex);
            }
            return lost;
        }

        internal static int SharedLost() => Run(Microsoft.Wpf.Interop.WebGpu.Direct3D.D3DDevice.SharedGpu, 3, null, false);

        [Fact]
        public void WriteTexture_Arrives()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("WRITETEXTURE_PROBE") != null, "a probe");
            const int N = 200;
            var lines = new List<string>();
            lines.Add($"the shim's shared device, after whatever ran before: {Run(Microsoft.Wpf.Interop.WebGpu.Direct3D.D3DDevice.SharedGpu, N, null, false)} of {N} lost");
            {
                // a compute pass writing a storage buffer, and in the same encoder a copy of it into a
                // MapRead buffer (what a tessellated draw does with its tess factors)
                var shared = Microsoft.Wpf.Interop.WebGpu.Direct3D.D3DDevice.SharedGpu;
                var device = new Microsoft.Wpf.Interop.WebGpu.Direct3D.D3DDevice(shared, ownsGpu: false, Microsoft.Wpf.Interop.WebGpu.Direct3D.D3D_FEATURE_LEVEL.L11_0, 0, null);
                IntPtr module = device.Immediate.CreateModule("@group(0) @binding(0) var<storage, read_write> b: array<u32>; @compute @workgroup_size(64) fn main(@builtin(global_invocation_id) g: vec3<u32>) { b[g.x] = g.x; }", "probe", out _);
                var entry = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &entry };
                IntPtr bgl = wgpuDeviceCreateBindGroupLayout(shared.Device, &ld);
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
                IntPtr pl = wgpuDeviceCreatePipelineLayout(shared.Device, &pld);
                byte[] mainName = System.Text.Encoding.UTF8.GetBytes("main");
                IntPtr pipeline;
                fixed (byte* m = mainName)
                {
                    var cd = new Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.WGPUComputePipelineDescriptor { layout = pl, compute = new Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.WGPUComputeState { module = module, entryPoint = new WGPUStringView { data = m, length = 4 } } };
                    pipeline = Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuDeviceCreateComputePipeline(shared.Device, &cd);
                }
                IntPtr sb = shared.CreateBuffer(256, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
                IntPtr rb = shared.CreateBuffer(4096, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
                var be = new WGPUBindGroupEntry { binding = 0, buffer = sb, offset = 0, size = 256 };
                var bd = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 1, entries = &be };
                IntPtr bg = wgpuDeviceCreateBindGroup(shared.Device, &bd);
                void Dispatch(bool copy, bool split = false)
                {
                    IntPtr e = wgpuDeviceCreateCommandEncoder(shared.Device, IntPtr.Zero);
                    IntPtr pass = Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuCommandEncoderBeginComputePass(e, null);
                    Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuComputePassEncoderSetPipeline(pass, pipeline);
                    Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);
                    Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                    Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuComputePassEncoderEnd(pass);
                    Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute.wgpuComputePassEncoderRelease(pass);
                    if (copy && split) { Submit(shared, e); e = wgpuDeviceCreateCommandEncoder(shared.Device, IntPtr.Zero); }
                    if (copy) wgpuCommandEncoderCopyBufferToBuffer(e, sb, 0, rb, 0, 256);
                    Submit(shared, e);
                    wgpuDevicePoll(shared.Device, WGPU_TRUE, null);
                }
                Dispatch(copy: false);
                lines.Add($"after a compute pass: {Run(shared, N, null, false)} of {N} lost");
                Dispatch(copy: true, split: true);
                lines.Add($"after a compute pass, and a copy into a MapRead buffer in the next submission: {Run(shared, N, null, false)} of {N} lost");
                Dispatch(copy: true);
                lines.Add($"after a compute pass and a copy into a MapRead buffer: {Run(shared, N, null, false)} of {N} lost");
                shared.MapRead(rb, 256);
                lines.Add($"  ... after mapping that buffer: {Run(shared, N, null, false)} of {N} lost");
                Dispatch(copy: false);
                lines.Add($"  ... after another compute pass: {Run(shared, N, null, false)} of {N} lost");
                {
                    IntPtr t = MakeTexture(shared);
                    IntPtr staging = shared.CreateBufferMapped(new byte[256 * S], WGPUBufferUsage.CopySrc);
                    IntPtr e = wgpuDeviceCreateCommandEncoder(shared.Device, IntPtr.Zero);
                    var src = new WGPUTexelCopyBufferInfo { buffer = staging, layout = new WGPUTexelCopyBufferLayout { bytesPerRow = 256, rowsPerImage = S } };
                    var dst = new WGPUTexelCopyTextureInfo { texture = t, aspect = WGPUTextureAspect.All };
                    var size = new WGPUExtent3D { width = S, height = S, depthOrArrayLayers = 1 };
                    wgpuCommandEncoderCopyBufferToTexture(e, &src, &dst, &size);
                    Submit(shared, e);
                    wgpuBufferRelease(staging);
                    wgpuTextureRelease(t);
                }
                lines.Add($"  ... after a copyBufferToTexture: {Run(shared, N, null, false)} of {N} lost");
                device.Release();
            }
            using (var gpu = WgpuContext.Create())
            {
                lines.Add($"one device: {Run(gpu, N, null, false)} of {N} lost");
                lines.Add($"one device, an encoder open across the write: {Run(gpu, N, null, true)} of {N} lost");
                lines.Add($"one device, a submission in between: {Run(gpu, N, _ => Submit(gpu, wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero)), false)} of {N} lost");
                lines.Add($"one device, a waiting poll in between: {Run(gpu, N, _ => wgpuDevicePoll(gpu.Device, WGPU_TRUE, null), false)} of {N} lost");
                lines.Add($"one device, a copy into a MapRead buffer and a waiting poll in between: {Run(gpu, N, _ => { IntPtr src = gpu.CreateBuffer(256, WGPUBufferUsage.CopySrc | WGPUBufferUsage.Storage); IntPtr rb = gpu.CreateBuffer(256, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst); IntPtr e = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero); wgpuCommandEncoderCopyBufferToBuffer(e, src, 0, rb, 0, 256); Submit(gpu, e); wgpuDevicePoll(gpu.Device, WGPU_TRUE, null); gpu.MapRead(rb, 256); wgpuBufferRelease(src); wgpuBufferRelease(rb); }, false)} of {N} lost");
                lines.Add($"and afterwards: {Run(gpu, N, null, false)} of {N} lost");
                {
                    // a copy into a MapRead buffer that is never mapped
                    IntPtr src = gpu.CreateBuffer(256, WGPUBufferUsage.CopySrc | WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
                    IntPtr rb = gpu.CreateBuffer(4096, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
                    gpu.WriteBuffer(src, new byte[256]);
                    IntPtr e = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero);
                    wgpuCommandEncoderCopyBufferToBuffer(e, src, 0, rb, 0, 256);
                    Submit(gpu, e);
                    wgpuDevicePoll(gpu.Device, WGPU_TRUE, null);
                    lines.Add($"after one copy into a MapRead buffer, never mapped: {Run(gpu, N, null, false)} of {N} lost");
                    gpu.MapRead(rb, 256);
                    lines.Add($"after mapping it: {Run(gpu, N, null, false)} of {N} lost");
                }
                using (var other = WgpuContext.Create())
                {
                    lines.Add($"another device alive: {Run(gpu, N, null, false)} of {N} lost");
                    IntPtr t2 = MakeTexture(other);
                    var px2 = new uint[S * S];
                    lines.Add($"another device used in between: {Run(gpu, N, _ => { Write(other, t2, px2); Submit(other, wgpuDeviceCreateCommandEncoder(other.Device, IntPtr.Zero)); }, false)} of {N} lost");
                    wgpuTextureRelease(t2);
                }
                lines.Add($"after the other device was disposed: {Run(gpu, N, null, false)} of {N} lost");
            }
            foreach (string l in lines) Console.WriteLine("  " + l);
        }
    }
}

namespace WgpuInterop.Tests.Direct3D
{
    /// <summary>WRITETEXTURE_WATCH=1: after every test, whether writeTexture on the shim's shared
    /// device still arrives; the first test after which it does not is named.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class WriteTextureWatchAttribute : Xunit.v3.BeforeAfterTestAttribute
    {
        private static bool s_reported;
        private static readonly bool s_on = Environment.GetEnvironmentVariable("WRITETEXTURE_WATCH") != null;

        public override void Before(System.Reflection.MethodInfo methodUnderTest, Xunit.v3.IXunitTest test)
        {
            if (s_on && !s_reported && WriteTextureProbeTests.SharedLost() > 0) { s_reported = true; Console.WriteLine($"WATCH: writeTexture lost BEFORE {test.TestDisplayName}"); }
        }

        public override void After(System.Reflection.MethodInfo methodUnderTest, Xunit.v3.IXunitTest test)
        {
            if (s_on && !s_reported && WriteTextureProbeTests.SharedLost() > 0) { s_reported = true; Console.WriteLine($"WATCH: writeTexture lost after {test.TestDisplayName}"); }
        }
    }
}
