// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WasmSpike: proves the browser WebGPU backend end-to-end without WPF.
// Boot -> WgpuBrowser.InitializeAsync -> WgpuContext.Create() (the UNCHANGED
// desktop code path, completing synchronously against pre-acquired handles) ->
// canvas surface via NativePlatform -> configure -> animated clear + triangle.
//
// Success criteria (asserted by the driver via console output):
//   "SPIKE: context ok ..." then "SPIKE OK frame=N" lines with no exceptions.
//

using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

internal static class Spike
{
    private const string TriangleWgsl = @"
struct VsOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) color : vec4<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>) -> VsOut {
    var o : VsOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.color = color;
    return o;
}

@fragment
fn fs_main(v : VsOut) -> @location(0) vec4<f32> {
    return v.color;
}
";

    private static async Task<int> Main()
    {
        try
        {
            Console.WriteLine("SPIKE: initializing WebGPU...");
            await WgpuBrowser.InitializeAsync();

            WgpuContext ctx = WgpuContext.Create();
            Console.WriteLine($"SPIKE: context ok {ctx.AdapterDescription}");

            // Canvas handle 1 is registered by main.js in globalThis.__wpfCanvases.
            IntPtr surface = NativePlatform.CreateWindowSurface(ctx.Instance, (IntPtr)1);
            ConfigureSurface(ctx, surface, 640, 480);

            IntPtr shader = CreateShader(ctx.Device);
            IntPtr pipeline = CreateTrianglePipeline(ctx.Device, shader, WGPUTextureFormat.RGBA8UnormSrgb);

            // Interleaved: pos.xy, color.rgba — one triangle.
            float[] verts =
            {
                 0.0f,  0.7f,   1f, 0.2f, 0.2f, 1f,
                -0.7f, -0.7f,   0.2f, 1f, 0.2f, 1f,
                 0.7f, -0.7f,   0.2f, 0.4f, 1f, 1f,
            };
            IntPtr vbuf = ctx.CreateBuffer((ulong)(verts.Length * sizeof(float)), WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst);
            byte[] vbytes = new byte[verts.Length * sizeof(float)];
            Buffer.BlockCopy(verts, 0, vbytes, 0, vbytes.Length);
            ctx.WriteBuffer(vbuf, vbytes);

            uint[] indices = { 0, 1, 2 };
            IntPtr ibuf = ctx.CreateBuffer((ulong)(indices.Length * sizeof(uint)), WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
            byte[] ibytes = new byte[indices.Length * sizeof(uint)];
            Buffer.BlockCopy(indices, 0, ibytes, 0, ibytes.Length);
            ctx.WriteBuffer(ibuf, ibytes);

            for (int frame = 0; frame < 300; frame++)
            {
                RenderFrame(ctx, surface, pipeline, vbuf, ibuf, frame);
                if (frame % 60 == 0)
                    Console.WriteLine($"SPIKE OK frame={frame}");
                await Task.Delay(16);
            }

            Console.WriteLine("SPIKE DONE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPIKE FAILED: {ex}");
            return 1;
        }
    }

    private static unsafe void ConfigureSurface(WgpuContext ctx, IntPtr surface, int width, int height)
    {
        var config = new WGPUSurfaceConfiguration
        {
            device = ctx.Device,
            format = WGPUTextureFormat.RGBA8UnormSrgb,
            usage = WGPUTextureUsage.RenderAttachment,
            width = (uint)width,
            height = (uint)height,
            alphaMode = WGPUCompositeAlphaMode.Auto,
            presentMode = WGPUPresentMode.Fifo,
        };
        wgpuSurfaceConfigure(surface, &config);
    }

    private static unsafe void RenderFrame(WgpuContext ctx, IntPtr surface, IntPtr pipeline, IntPtr vbuf, IntPtr ibuf, int frame)
    {
        WGPUSurfaceTexture st;
        wgpuSurfaceGetCurrentTexture(surface, &st);
        if (st.texture == IntPtr.Zero)
        {
            Console.WriteLine($"SPIKE: no surface texture (status={st.status})");
            return;
        }

        IntPtr view = wgpuTextureCreateView(st.texture, IntPtr.Zero);
        IntPtr encoder = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);

        double t = frame / 60.0;
        var colorAttachment = new WGPURenderPassColorAttachment
        {
            view = view,
            depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
            loadOp = WGPULoadOp.Clear,
            storeOp = WGPUStoreOp.Store,
            clearValue = new WGPUColor
            {
                r = 0.5 + 0.5 * Math.Sin(t),
                g = 0.5 + 0.5 * Math.Sin(t + 2.1),
                b = 0.5 + 0.5 * Math.Sin(t + 4.2),
                a = 1,
            },
        };
        var passDesc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &colorAttachment };
        IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);

        wgpuRenderPassEncoderSetPipeline(pass, pipeline);
        wgpuRenderPassEncoderSetVertexBuffer(pass, 0, vbuf, 0, ulong.MaxValue);
        wgpuRenderPassEncoderSetIndexBuffer(pass, ibuf, WGPUIndexFormat.Uint32, 0, ulong.MaxValue);
        wgpuRenderPassEncoderDrawIndexed(pass, 3, 1, 0, 0, 0);
        wgpuRenderPassEncoderEnd(pass);

        IntPtr cmd = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
        IntPtr* cmds = stackalloc IntPtr[1];
        cmds[0] = cmd;
        wgpuQueueSubmit(ctx.Queue, 1, cmds);
        wgpuSurfacePresent(surface);

        wgpuCommandBufferRelease(cmd);
        wgpuCommandEncoderRelease(encoder);
        wgpuRenderPassEncoderRelease(pass);
        wgpuTextureViewRelease(view);
        wgpuTextureRelease(st.texture);
    }

    private static unsafe IntPtr CreateShader(IntPtr device)
    {
        byte[] wgsl = Encoding.UTF8.GetBytes(TriangleWgsl);
        fixed (byte* p = wgsl)
        {
            var src = new WGPUShaderSourceWGSL
            {
                chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                code = new WGPUStringView { data = p, length = (nuint)wgsl.Length },
            };
            var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
            return wgpuDeviceCreateShaderModule(device, &desc);
        }
    }

    private static unsafe IntPtr CreateTrianglePipeline(IntPtr device, IntPtr shader, WGPUTextureFormat format)
    {
        byte[] vsEntry = Encoding.UTF8.GetBytes("vs_main");
        byte[] fsEntry = Encoding.UTF8.GetBytes("fs_main");
        fixed (byte* pVs = vsEntry)
        fixed (byte* pFs = fsEntry)
        {
            var attributes = stackalloc WGPUVertexAttribute[2];
            attributes[0] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 0, shaderLocation = 0 };
            attributes[1] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x4, offset = 2 * sizeof(float), shaderLocation = 1 };

            var bufferLayout = new WGPUVertexBufferLayout
            {
                stepMode = WGPUVertexStepMode.Vertex,
                arrayStride = 6 * sizeof(float),
                attributeCount = 2,
                attributes = attributes,
            };

            var blend = new WGPUBlendState
            {
                color = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                alpha = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
            };
            var colorTarget = new WGPUColorTargetState { format = format, blend = &blend, writeMask = WGPUColorWriteMask_All };
            var fragment = new WGPUFragmentState
            {
                module = shader,
                entryPoint = new WGPUStringView { data = pFs, length = (nuint)fsEntry.Length },
                targetCount = 1,
                targets = &colorTarget,
            };

            var desc = new WGPURenderPipelineDescriptor
            {
                layout = IntPtr.Zero,
                vertex = new WGPUVertexState
                {
                    module = shader,
                    entryPoint = new WGPUStringView { data = pVs, length = (nuint)vsEntry.Length },
                    bufferCount = 1,
                    buffers = &bufferLayout,
                },
                primitive = new WGPUPrimitiveState
                {
                    topology = WGPUPrimitiveTopology.TriangleList,
                    frontFace = WGPUFrontFace.CCW,
                    cullMode = WGPUCullMode.None,
                },
                multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                fragment = &fragment,
            };
            return wgpuDeviceCreateRenderPipeline(device, &desc);
        }
    }
}
