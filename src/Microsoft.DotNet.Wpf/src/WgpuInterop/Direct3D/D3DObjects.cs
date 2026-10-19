// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The immutable D3D11 objects: state objects, shaders and input layouts. They hold their descriptions;
// the context turns the bound ones into a WebGPU pipeline at draw time.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe class D3DBlendState : D3DChild
    {
        public readonly D3D11_BLEND_DESC Desc;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11BlendState", Vtbl.ID3D11BlendState1.SlotCount, Vtbl.ID3D11BlendState.IID, Vtbl.ID3D11BlendState1.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11BlendState.GetDesc, (delegate* unmanaged<IntPtr, D3D11_BLEND_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DBlendState(D3DDevice d, D3D11_BLEND_DESC desc) : base(d, s_vt) { Desc = desc; }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_BLEND_DESC* desc) { var o = From<D3DBlendState>(self); if (o != null && desc != null) *desc = o.Desc; }
    }

    internal sealed unsafe class D3DDepthStencilState : D3DChild
    {
        public readonly D3D11_DEPTH_STENCIL_DESC Desc;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11DepthStencilState", Vtbl.ID3D11DepthStencilState.SlotCount, Vtbl.ID3D11DepthStencilState.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11DepthStencilState.GetDesc, (delegate* unmanaged<IntPtr, D3D11_DEPTH_STENCIL_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DDepthStencilState(D3DDevice d, D3D11_DEPTH_STENCIL_DESC desc) : base(d, s_vt) { Desc = desc; }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_DEPTH_STENCIL_DESC* desc) { var o = From<D3DDepthStencilState>(self); if (o != null && desc != null) *desc = o.Desc; }
    }

    internal sealed unsafe class D3DRasterizerState : D3DChild
    {
        public readonly D3D11_RASTERIZER_DESC Desc;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11RasterizerState", Vtbl.ID3D11RasterizerState2.SlotCount,
                Vtbl.ID3D11RasterizerState.IID, Vtbl.ID3D11RasterizerState1.IID, Vtbl.ID3D11RasterizerState2.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11RasterizerState.GetDesc, (delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DRasterizerState(D3DDevice d, D3D11_RASTERIZER_DESC desc) : base(d, s_vt) { Desc = desc; }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_RASTERIZER_DESC* desc) { var o = From<D3DRasterizerState>(self); if (o != null && desc != null) *desc = o.Desc; }
    }

    internal sealed unsafe class D3DSamplerState : D3DChild
    {
        public readonly D3D11_SAMPLER_DESC Desc;
        private IntPtr _sampler;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11SamplerState", Vtbl.ID3D11SamplerState.SlotCount, Vtbl.ID3D11SamplerState.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11SamplerState.GetDesc, (delegate* unmanaged<IntPtr, D3D11_SAMPLER_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DSamplerState(D3DDevice d, D3D11_SAMPLER_DESC desc) : base(d, s_vt) { Desc = desc; }

        public bool IsComparison => (Desc.Filter & 0x180) == 0x80;

        private static WGPUAddressMode Address(uint a)
        {
            switch (a)
            {
                case 1: return WGPUAddressMode.Repeat;
                case 2: return WGPUAddressMode.MirrorRepeat;
                case 4: D3DLog.Once("border", "border address mode is clamped to the edge"); return WGPUAddressMode.ClampToEdge;
                case 5: return WGPUAddressMode.MirrorRepeat;
                default: return WGPUAddressMode.ClampToEdge;
            }
        }

        /// <summary>The WebGPU sampler (made once).</summary>
        public IntPtr Sampler(bool comparison) => SamplerFor(Device, Desc, comparison, ref _sampler);

        internal static IntPtr SamplerFor(D3DDevice device, in D3D11_SAMPLER_DESC desc, bool comparison, ref IntPtr cache)
        {
            if (cache != IntPtr.Zero) return cache;
            uint f = desc.Filter;
            bool aniso = (f & 0x40) != 0;
            var d = new WGPUSamplerDescriptor
            {
                addressModeU = Address(desc.AddressU), addressModeV = Address(desc.AddressV), addressModeW = Address(desc.AddressW),
                magFilter = aniso || (f & 0x4) != 0 ? WGPUFilterMode.Linear : WGPUFilterMode.Nearest,
                minFilter = aniso || (f & 0x10) != 0 ? WGPUFilterMode.Linear : WGPUFilterMode.Nearest,
                mipmapFilter = aniso || (f & 0x1) != 0 ? WGPUMipmapFilterMode.Linear : WGPUMipmapFilterMode.Nearest,
                lodMinClamp = Math.Max(0f, desc.MinLOD),
                lodMaxClamp = Math.Clamp(desc.MaxLOD, Math.Max(0f, desc.MinLOD), 32f),
                compare = comparison ? (WGPUCompareFunction)Math.Clamp(desc.ComparisonFunc, 1u, 8u) : WGPUCompareFunction.Undefined,
                maxAnisotropy = (ushort)(aniso ? Math.Clamp(desc.MaxAnisotropy, 1u, 16u) : 1u),
            };
            cache = wgpuDeviceCreateSampler(device.Gpu.Device, &d);
            return cache;
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_SAMPLER_DESC* desc) { var o = From<D3DSamplerState>(self); if (o != null && desc != null) *desc = o.Desc; }

        protected override void OnFinalRelease()
        {
            if (_sampler != IntPtr.Zero) wgpuSamplerRelease(_sampler);
            _sampler = IntPtr.Zero;
            base.OnFinalRelease();
        }
    }

    internal sealed unsafe class D3DShader : D3DChild
    {
        public readonly DxbcShader Code;
        private static readonly Vtable s_vs = Build("ID3D11VertexShader", Vtbl.ID3D11VertexShader.IID);
        private static readonly Vtable s_ps = Build("ID3D11PixelShader", Vtbl.ID3D11PixelShader.IID);
        private static readonly Vtable s_gs = Build("ID3D11GeometryShader", Vtbl.ID3D11GeometryShader.IID);
        private static readonly Vtable s_cs = Build("ID3D11ComputeShader", Vtbl.ID3D11ComputeShader.IID);
        private static readonly Vtable s_hs = Build("ID3D11HullShader", Vtbl.ID3D11HullShader.IID);
        private static readonly Vtable s_ds = Build("ID3D11DomainShader", Vtbl.ID3D11DomainShader.IID);

        private static Vtable Build(string name, Guid iid)
        {
            var vt = new Vtable(name, 7, iid, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            return vt;
        }

        /// <summary>A geometry shader made with CreateGeometryShaderWithStreamOutput (its code may be a
        /// vertex or domain shader's, standing in for a pass-through GS).</summary>
        public readonly StreamOutputDesc? StreamOutput;

        public D3DShader(D3DDevice d, DxbcShader code, StreamOutputDesc so) : base(d, s_gs)
        {
            Code = code;
            StreamOutput = so;
        }

        public D3DShader(D3DDevice d, DxbcShader code) : base(d, code.Stage switch
        {
            ShaderStage.Vertex => s_vs,
            ShaderStage.Pixel => s_ps,
            ShaderStage.Geometry => s_gs,
            ShaderStage.Compute => s_cs,
            ShaderStage.Hull => s_hs,
            ShaderStage.Domain => s_ds,
            _ => throw new NotSupportedException($"{code.Stage} shaders"),
        })
        {
            Code = code;
        }

        public ShaderStage Stage => StreamOutput != null ? ShaderStage.Geometry : Code.Stage;
    }

    internal sealed unsafe class D3DInputLayout : D3DChild
    {
        public struct Element
        {
            public string Semantic;
            public int Index;
            public DXGI_FORMAT Format;
            public int Slot;
            public uint Offset;             // D3D11_APPEND_ALIGNED_ELEMENT = 0xffffffff
            public bool PerInstance;
            public uint StepRate;
        }

        public readonly Element[] Elements;
        public readonly int Id;
        private static int s_ids;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11InputLayout", 7, Vtbl.ID3D11InputLayout.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            return vt;
        }

        public D3DInputLayout(D3DDevice d, Element[] elements) : base(d, s_vt)
        {
            // resolve D3D11_APPEND_ALIGNED_ELEMENT now: each slot's running offset
            var next = new Dictionary<int, uint>();
            for (int i = 0; i < elements.Length; i++)
            {
                ref var e = ref elements[i];
                uint at = next.TryGetValue(e.Slot, out uint n) ? n : 0;
                if (e.Offset == 0xFFFFFFFF) e.Offset = at;
                next[e.Slot] = e.Offset + Formats.TexelBytes(e.Format);
                if (Formats.Vertex(e.Format) == 0) throw new NotSupportedException($"input element format {e.Format}");
            }
            Elements = elements;
            Id = System.Threading.Interlocked.Increment(ref s_ids);
        }
    }
}
