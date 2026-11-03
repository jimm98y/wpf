// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A resolve is a render pass that loads the multisampled texture and names a resolve target, and that is
// all it does (ResolveSubresource, the GL head's multisample shadows). Qualcomm's Adreno (a Meta Quest's)
// skips the resolve of a pass with no draw in it: the target came out zero -- a D3DImage that DXEngine
// renders multisampled was transparent but for the odd tile of what the memory held before. Measured on
// the device (StorageProbe, WPF_WEBGPU_PROBE=1): the same pass resolves with one draw in it. So every
// resolve pass gets one, which reaches no pixel (outside the clip volume) and writes nothing if it did;
// on every head, so no head depends on what its driver does with an empty pass.
//

using System;
using System.Collections.Generic;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DDevice
    {
        private readonly Dictionary<WGPUTextureFormat, IntPtr> _drawNothing = new();
        private IntPtr _drawNothingModule, _drawNothingLayout;

        private const string DrawNothingWgsl = @"
@vertex fn vs() -> @builtin(position) vec4<f32> { return vec4<f32>(2.0, 2.0, 0.0, 1.0); }
@fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(0.0); }
";

        /// <summary>A draw into <paramref name="pass"/> (a 4x color attachment of <paramref name="format"/>, nothing
        /// else) that reaches no pixel: a resolve pass's, so that the driver does not skip the resolve.</summary>
        public void DrawNothing(IntPtr pass, WGPUTextureFormat format)
        {
            IntPtr pipeline;
            lock (Sync)
            {
                if (!_drawNothing.TryGetValue(format, out pipeline))
                {
                    pipeline = CreateDrawNothing(format);
                    _drawNothing[format] = pipeline;
                }
            }
            if (pipeline == IntPtr.Zero) return;
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
        }

        private IntPtr CreateDrawNothing(WGPUTextureFormat format)
        {
            if (_drawNothingModule == IntPtr.Zero)
            {
                byte[] code = Encoding.UTF8.GetBytes(DrawNothingWgsl);
                fixed (byte* c = code)
                {
                    var src = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = c, length = (nuint)code.Length } };
                    var md = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                    _drawNothingModule = wgpuDeviceCreateShaderModule(Gpu.Device, &md);
                }
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 0 };
                _drawNothingLayout = wgpuDeviceCreatePipelineLayout(Gpu.Device, &pld);
            }
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = format, writeMask = 0 };
                var frag = new WGPUFragmentState { module = _drawNothingModule, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = _drawNothingLayout,
                    vertex = new WGPUVertexState { module = _drawNothingModule, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 4, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                return wgpuDeviceCreateRenderPipeline(Gpu.Device, &desc);
            }
        }
    }
}
