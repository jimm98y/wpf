// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The pipeline-state descriptions of d3d11.h, as laid out there.
//

using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RENDER_TARGET_BLEND_DESC
    {
        public int BlendEnable;
        public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
        public byte RenderTargetWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_BLEND_DESC
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public D3D11_RENDER_TARGET_BLEND_DESC RT0, RT1, RT2, RT3, RT4, RT5, RT6, RT7;

        public D3D11_RENDER_TARGET_BLEND_DESC Target(int i)
        {
            fixed (D3D11_RENDER_TARGET_BLEND_DESC* p = &RT0) return p[IndependentBlendEnable != 0 ? i : 0];
        }

        public static D3D11_BLEND_DESC Default()
        {
            var d = new D3D11_BLEND_DESC();
            var rt = new D3D11_RENDER_TARGET_BLEND_DESC { SrcBlend = 2, DestBlend = 1, BlendOp = 1, SrcBlendAlpha = 2, DestBlendAlpha = 1, BlendOpAlpha = 1, RenderTargetWriteMask = 0xF };
            d.RT0 = d.RT1 = d.RT2 = d.RT3 = d.RT4 = d.RT5 = d.RT6 = d.RT7 = rt;
            return d;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_DEPTH_STENCILOP_DESC { public uint StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_DEPTH_STENCIL_DESC
    {
        public int DepthEnable;
        public uint DepthWriteMask;          // 0 zero, 1 all
        public uint DepthFunc;
        public int StencilEnable;
        public byte StencilReadMask, StencilWriteMask;
        public D3D11_DEPTH_STENCILOP_DESC FrontFace, BackFace;

        public static D3D11_DEPTH_STENCIL_DESC Default() => new()
        {
            DepthEnable = 1, DepthWriteMask = 1, DepthFunc = 2 /*LESS*/, StencilEnable = 0, StencilReadMask = 0xFF, StencilWriteMask = 0xFF,
            FrontFace = new D3D11_DEPTH_STENCILOP_DESC { StencilFailOp = 1, StencilDepthFailOp = 1, StencilPassOp = 1, StencilFunc = 8 },
            BackFace = new D3D11_DEPTH_STENCILOP_DESC { StencilFailOp = 1, StencilDepthFailOp = 1, StencilPassOp = 1, StencilFunc = 8 },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RASTERIZER_DESC
    {
        public uint FillMode;                // 2 wireframe, 3 solid
        public uint CullMode;                // 1 none, 2 front, 3 back
        public int FrontCounterClockwise;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public int DepthClipEnable;
        public int ScissorEnable;
        public int MultisampleEnable;
        public int AntialiasedLineEnable;

        public static D3D11_RASTERIZER_DESC Default() => new() { FillMode = 3, CullMode = 3, DepthClipEnable = 1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_SAMPLER_DESC
    {
        public uint Filter;
        public uint AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public uint ComparisonFunc;
        public float Border0, Border1, Border2, Border3;
        public float MinLOD, MaxLOD;

        public static D3D11_SAMPLER_DESC Default() => new()
        {
            Filter = 0x15 /*MIN_MAG_MIP_LINEAR*/, AddressU = 3, AddressV = 3, AddressW = 3, MaxAnisotropy = 1, ComparisonFunc = 1,
            Border0 = 1, Border1 = 1, Border2 = 1, Border3 = 1, MinLOD = -float.MaxValue, MaxLOD = float.MaxValue,
        };
    }
}
