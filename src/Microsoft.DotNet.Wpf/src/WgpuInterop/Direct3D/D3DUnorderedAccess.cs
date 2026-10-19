// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Unordered-access views and the compute-pass entry points of wgpu-native the shim needs
// (Documentation/d3d11-webgpu-shim.md, section 9). A buffer UAV is a storage-buffer range of its
// buffer, with a 4-byte counter buffer of its own for the append/counter forms; a texture UAV is a
// storage-texture view of one mip (and a range of layers or 3D slices).
//

using System;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe class D3DUnorderedAccessView : D3DView
    {
        public readonly D3D11_VIEW_DESC Desc;
        /// <summary>A buffer view's range in bytes.</summary>
        public readonly ulong Offset, Size;
        /// <summary>D3D11_BUFFER_UAV_FLAG: 1 raw, 2 append, 4 counter.</summary>
        public readonly uint Flags;
        /// <summary>The hidden counter of an append/counter view (4 bytes).</summary>
        public IntPtr Counter;
        /// <summary>A texture view's storage view.</summary>
        public IntPtr View;
        public readonly WGPUTextureFormat Format;
        public readonly uint Mip;

        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11UnorderedAccessView", Vtbl.ID3D11UnorderedAccessView1.SlotCount,
                Vtbl.ID3D11UnorderedAccessView.IID, Vtbl.ID3D11UnorderedAccessView1.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillView(vt);
            vt.Set(Vtbl.ID3D11UnorderedAccessView.GetDesc, (delegate* unmanaged<IntPtr, D3D11_VIEW_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DUnorderedAccessView(D3DDevice device, D3DResource resource, D3D11_VIEW_DESC? desc) : base(device, resource, s_vt)
        {
            if (resource is D3DBuffer b)
            {
                if ((b.Desc.BindFlags & D3D11_BIND.UNORDERED_ACCESS) == 0) throw new ArgumentException("the buffer has no UNORDERED_ACCESS binding");
                if (desc == null) throw new ArgumentException("a buffer UAV needs a description");
                var d = desc.Value;
                if (d.ViewDimension != 1) throw new ArgumentException($"buffer UAV dimension {d.ViewDimension}");
                Desc = d;
                Flags = d.U2;
                bool structured = (b.Desc.MiscFlags & D3D11_RESOURCE_MISC.BUFFER_STRUCTURED) != 0;
                uint element = (Flags & 1) != 0 ? 4u : structured ? Math.Max(4u, b.Desc.StructureByteStride) : Math.Max(1u, Formats.TexelBytes(d.Format));
                Offset = (ulong)d.U0 * element;
                Size = (ulong)d.U1 * element;
                if (Offset + Size > b.Size) throw new ArgumentException("the UAV's range runs past the buffer");
                if ((Flags & 6) != 0)
                {
                    var bd = new WGPUBufferDescriptor { usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc, size = 4 };
                    Counter = wgpuDeviceCreateBuffer(Device.Gpu.Device, &bd);
                }
                return;
            }
            if (resource is D3DTexture2D t)
            {
                if ((t.Desc.BindFlags & D3D11_BIND.UNORDERED_ACCESS) == 0) throw new ArgumentException("the texture has no UNORDERED_ACCESS binding");
                var d = desc ?? new D3D11_VIEW_DESC
                {
                    Format = t.Desc.Format,
                    ViewDimension = t.Kind == 3 ? 8u : t.Kind == 1 ? (t.Desc.ArraySize > 1 ? 3u : 2u) : (t.Desc.ArraySize > 1 ? 5u : 4u),
                    U0 = 0, U1 = 0, U2 = t.Kind == 3 ? t.Depth : t.Desc.ArraySize,
                };
                if (d.Format == DXGI_FORMAT.UNKNOWN) d.Format = t.Desc.Format;
                Desc = d;
                Format = Formats.Texture(d.Format);
                Mip = d.U0;
                // (mip, first, count) by dimension: 1D/2D just a mip; 1D/2D arrays mip, first, size; 3D mip, first W, W size
                uint first = 0, count = 1;
                var dim = WGPUTextureViewDimension._2D;
                switch (d.ViewDimension)
                {
                    case 3: case 5: first = d.U1; count = d.U2; dim = WGPUTextureViewDimension._2DArray; break;
                    case 8: dim = WGPUTextureViewDimension._3D; break;
                }
                View = CreateTextureView(t.Gpu, Format, dim, Mip, 1, first, Math.Max(1, count));
                return;
            }
            throw new NotSupportedException($"unordered access view of {resource.GetType().Name}");
        }

        /// <summary>The WGSL texel format a storage texture of this view is declared with.</summary>
        public string WgslFormat => Format switch
        {
            TexFmt.R32Float => "r32float", TexFmt.R32Uint => "r32uint", TexFmt.R32Sint => "r32sint",
            TexFmt.RGBA8Unorm => "rgba8unorm", TexFmt.RGBA8Snorm => "rgba8snorm", TexFmt.RGBA8Uint => "rgba8uint", TexFmt.RGBA8Sint => "rgba8sint",
            TexFmt.BGRA8Unorm => "bgra8unorm",
            TexFmt.RGBA16Uint => "rgba16uint", TexFmt.RGBA16Sint => "rgba16sint", TexFmt.RGBA16Float => "rgba16float",
            TexFmt.RG32Float => "rg32float", TexFmt.RG32Uint => "rg32uint", TexFmt.RG32Sint => "rg32sint",
            TexFmt.RGBA32Float => "rgba32float", TexFmt.RGBA32Uint => "rgba32uint", TexFmt.RGBA32Sint => "rgba32sint",
            TexFmt.R16Float => "r16float", TexFmt.R8Unorm => "r8unorm", TexFmt.RG8Unorm => "rg8unorm", TexFmt.RG16Float => "rg16float",
            TexFmt.RGB10A2Unorm => "rgb10a2unorm", TexFmt.RG11B10Ufloat => "rg11b10ufloat",
            _ => throw new NotSupportedException($"a storage texture of format {Desc.Format}"),
        };

        /// <summary>A typed buffer view's element size in 32-bit words (the translator's specialization).</summary>
        public int BufferWords => Math.Max(1, (int)Formats.TexelBytes(Desc.Format) / 4);

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_VIEW_DESC* desc)
        {
            var v = From<D3DUnorderedAccessView>(self);
            if (v != null && desc != null) *desc = v.Desc;
        }

        protected override void OnFinalRelease()
        {
            if (View != IntPtr.Zero) wgpuTextureViewRelease(View);
            if (Counter != IntPtr.Zero) wgpuBufferRelease(Counter);
            View = Counter = IntPtr.Zero;
            base.OnFinalRelease();
        }
    }

    internal static unsafe class WgpuCompute
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUConstantEntry { public IntPtr nextInChain; public WGPUStringView key; public double value; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUComputeState { public IntPtr nextInChain; public IntPtr module; public WGPUStringView entryPoint; public nuint constantCount; public WGPUConstantEntry* constants; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUComputePipelineDescriptor { public IntPtr nextInChain; public WGPUStringView label; public IntPtr layout; public WGPUComputeState compute; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUComputePassDescriptor { public IntPtr nextInChain; public WGPUStringView label; public IntPtr timestampWrites; }

        private const string Lib = Microsoft.Wpf.Interop.WebGpu.Wgpu.Library;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wgpuDeviceCreateComputePipeline(IntPtr device, WGPUComputePipelineDescriptor* d);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePipelineRelease(IntPtr pipeline);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wgpuCommandEncoderBeginComputePass(IntPtr encoder, WGPUComputePassDescriptor* d);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderSetPipeline(IntPtr pass, IntPtr pipeline);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderSetBindGroup(IntPtr pass, uint index, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderDispatchWorkgroups(IntPtr pass, uint x, uint y, uint z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderDispatchWorkgroupsIndirect(IntPtr pass, IntPtr buffer, ulong offset);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderEnd(IntPtr pass);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuComputePassEncoderRelease(IntPtr pass);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuCommandEncoderClearBuffer(IntPtr encoder, IntPtr buffer, ulong offset, ulong size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuRenderPassEncoderDrawIndirect(IntPtr pass, IntPtr buffer, ulong offset);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void wgpuRenderPassEncoderDrawIndexedIndirect(IntPtr pass, IntPtr buffer, ulong offset);
    }
}
