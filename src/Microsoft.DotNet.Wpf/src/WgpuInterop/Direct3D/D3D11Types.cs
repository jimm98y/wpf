// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 11 / DXGI structures and enumerations the shim reads and writes, laid out as d3d11.h
// and dxgi*.h declare them, and the mapping of DXGI formats to WebGPU's.
//

using System;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal enum D3D11_USAGE : uint { DEFAULT = 0, IMMUTABLE = 1, DYNAMIC = 2, STAGING = 3 }

    [Flags]
    internal enum D3D11_BIND : uint
    {
        VERTEX_BUFFER = 0x1, INDEX_BUFFER = 0x2, CONSTANT_BUFFER = 0x4, SHADER_RESOURCE = 0x8,
        STREAM_OUTPUT = 0x10, RENDER_TARGET = 0x20, DEPTH_STENCIL = 0x40, UNORDERED_ACCESS = 0x80, DECODER = 0x200, VIDEO_ENCODER = 0x400,
    }

    [Flags]
    internal enum D3D11_CPU_ACCESS : uint { WRITE = 0x10000, READ = 0x20000 }

    [Flags]
    internal enum D3D11_RESOURCE_MISC : uint
    {
        GENERATE_MIPS = 0x1, SHARED = 0x2, TEXTURECUBE = 0x4, DRAWINDIRECT_ARGS = 0x10, BUFFER_ALLOW_RAW_VIEWS = 0x20,
        BUFFER_STRUCTURED = 0x40, RESOURCE_CLAMP = 0x80, SHARED_KEYEDMUTEX = 0x100, GDI_COMPATIBLE = 0x200, SHARED_NTHANDLE = 0x800,
    }

    internal enum D3D11_MAP : uint { READ = 1, WRITE = 2, READ_WRITE = 3, WRITE_DISCARD = 4, WRITE_NO_OVERWRITE = 5 }

    internal enum D3D11_RESOURCE_DIMENSION : uint { UNKNOWN = 0, BUFFER = 1, TEXTURE1D = 2, TEXTURE2D = 3, TEXTURE3D = 4 }

    internal enum D3D_PRIMITIVE_TOPOLOGY : uint
    {
        UNDEFINED = 0, POINTLIST = 1, LINELIST = 2, LINESTRIP = 3, TRIANGLELIST = 4, TRIANGLESTRIP = 5,
        LINELIST_ADJ = 10, LINESTRIP_ADJ = 11, TRIANGLELIST_ADJ = 12, TRIANGLESTRIP_ADJ = 13,
    }

    internal enum D3D_FEATURE_LEVEL : uint
    {
        L9_1 = 0x9100, L9_2 = 0x9200, L9_3 = 0x9300, L10_0 = 0xa000, L10_1 = 0xa100, L11_0 = 0xb000, L11_1 = 0xb100, L12_0 = 0xc000, L12_1 = 0xc100,
    }

    internal enum D3D_DRIVER_TYPE : uint { UNKNOWN = 0, HARDWARE = 1, REFERENCE = 2, NULL = 3, SOFTWARE = 4, WARP = 5 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SAMPLE_DESC { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public D3D11_USAGE Usage;
        public D3D11_BIND BindFlags;
        public D3D11_CPU_ACCESS CPUAccessFlags;
        public D3D11_RESOURCE_MISC MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SUBRESOURCE_DATA
    {
        public void* pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE1D_DESC
    {
        public uint Width, MipLevels, ArraySize;
        public DXGI_FORMAT Format;
        public D3D11_USAGE Usage;
        public D3D11_BIND BindFlags;
        public D3D11_CPU_ACCESS CPUAccessFlags;
        public D3D11_RESOURCE_MISC MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE3D_DESC
    {
        public uint Width, Height, Depth, MipLevels;
        public DXGI_FORMAT Format;
        public D3D11_USAGE Usage;
        public D3D11_BIND BindFlags;
        public D3D11_CPU_ACCESS CPUAccessFlags;
        public D3D11_RESOURCE_MISC MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public DXGI_FORMAT Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public D3D11_USAGE Usage;
        public D3D11_BIND BindFlags;
        public D3D11_CPU_ACCESS CPUAccessFlags;
        public D3D11_RESOURCE_MISC MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_MAPPED_SUBRESOURCE
    {
        public void* pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SO_DECLARATION_ENTRY
    {
        public uint Stream;
        public byte* SemanticName;
        public uint SemanticIndex;
        public byte StartComponent, ComponentCount, OutputSlot;
    }

    internal unsafe struct D3D11_INPUT_ELEMENT_DESC
    {
        public byte* SemanticName;
        public uint SemanticIndex;
        public DXGI_FORMAT Format;
        public uint InputSlot;
        public uint AlignedByteOffset;
        public uint InputSlotClass;          // 0 per vertex, 1 per instance
        public uint InstanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIEWPORT { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BOX { public uint left, top, front, right, bottom, back; }

    /// <summary>D3D11_RENDER_TARGET_VIEW_DESC, D3D11_SHADER_RESOURCE_VIEW_DESC and
    /// D3D11_DEPTH_STENCIL_VIEW_DESC share this shape: a format, a dimension, then a union of at
    /// most four UINTs (the depth view has a flags word before its union).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIEW_DESC
    {
        public DXGI_FORMAT Format;
        public uint ViewDimension;
        public uint U0, U1, U2, U3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_DEPTH_STENCIL_VIEW_DESC
    {
        public DXGI_FORMAT Format;
        public uint ViewDimension;
        public uint Flags;
        public uint U0, U1, U2;
    }

    internal enum DXGI_FORMAT : uint
    {
        UNKNOWN = 0,
        R32G32B32A32_TYPELESS = 1, R32G32B32A32_FLOAT = 2, R32G32B32A32_UINT = 3, R32G32B32A32_SINT = 4,
        R32G32B32_TYPELESS = 5, R32G32B32_FLOAT = 6, R32G32B32_UINT = 7, R32G32B32_SINT = 8,
        R16G16B16A16_TYPELESS = 9, R16G16B16A16_FLOAT = 10, R16G16B16A16_UNORM = 11, R16G16B16A16_UINT = 12, R16G16B16A16_SNORM = 13, R16G16B16A16_SINT = 14,
        R32G32_TYPELESS = 15, R32G32_FLOAT = 16, R32G32_UINT = 17, R32G32_SINT = 18,
        R32G8X24_TYPELESS = 19, D32_FLOAT_S8X24_UINT = 20, R32_FLOAT_X8X24_TYPELESS = 21, X32_TYPELESS_G8X24_UINT = 22,
        R10G10B10A2_TYPELESS = 23, R10G10B10A2_UNORM = 24, R10G10B10A2_UINT = 25, R11G11B10_FLOAT = 26,
        R8G8B8A8_TYPELESS = 27, R8G8B8A8_UNORM = 28, R8G8B8A8_UNORM_SRGB = 29, R8G8B8A8_UINT = 30, R8G8B8A8_SNORM = 31, R8G8B8A8_SINT = 32,
        R16G16_TYPELESS = 33, R16G16_FLOAT = 34, R16G16_UNORM = 35, R16G16_UINT = 36, R16G16_SNORM = 37, R16G16_SINT = 38,
        R32_TYPELESS = 39, D32_FLOAT = 40, R32_FLOAT = 41, R32_UINT = 42, R32_SINT = 43,
        R24G8_TYPELESS = 44, D24_UNORM_S8_UINT = 45, R24_UNORM_X8_TYPELESS = 46, X24_TYPELESS_G8_UINT = 47,
        R8G8_TYPELESS = 48, R8G8_UNORM = 49, R8G8_UINT = 50, R8G8_SNORM = 51, R8G8_SINT = 52,
        R16_TYPELESS = 53, R16_FLOAT = 54, D16_UNORM = 55, R16_UNORM = 56, R16_UINT = 57, R16_SNORM = 58, R16_SINT = 59,
        R8_TYPELESS = 60, R8_UNORM = 61, R8_UINT = 62, R8_SNORM = 63, R8_SINT = 64, A8_UNORM = 65,
        BC1_TYPELESS = 70, BC1_UNORM = 71, BC1_UNORM_SRGB = 72, BC2_TYPELESS = 73, BC2_UNORM = 74, BC2_UNORM_SRGB = 75,
        BC3_TYPELESS = 76, BC3_UNORM = 77, BC3_UNORM_SRGB = 78, BC4_TYPELESS = 79, BC4_UNORM = 80, BC4_SNORM = 81,
        BC5_TYPELESS = 82, BC5_UNORM = 83, BC5_SNORM = 84,
        B8G8R8A8_UNORM = 87, B8G8R8X8_UNORM = 88, B8G8R8A8_TYPELESS = 90, B8G8R8A8_UNORM_SRGB = 91, B8G8R8X8_TYPELESS = 92, B8G8R8X8_UNORM_SRGB = 93,
        BC6H_TYPELESS = 94, BC6H_UF16 = 95, BC6H_SF16 = 96, BC7_TYPELESS = 97, BC7_UNORM = 98, BC7_UNORM_SRGB = 99,
    }

    /// <summary>What a DXGI format is in WebGPU, and how big its texels are.</summary>
    internal static class Formats
    {
        /// <summary>The WebGPU texture format for a resource of this DXGI format. A typeless format is
        /// given its UNORM (or FLOAT) member: WebGPU has no typeless textures, and a view of another
        /// member of the family is a view format of the texture (viewFormats).</summary>
        public static WGPUTextureFormat Texture(DXGI_FORMAT f) => f switch
        {
            DXGI_FORMAT.R32G32B32A32_TYPELESS or DXGI_FORMAT.R32G32B32A32_FLOAT => TexFmt.RGBA32Float,
            DXGI_FORMAT.R32G32B32A32_UINT => TexFmt.RGBA32Uint,
            DXGI_FORMAT.R32G32B32A32_SINT => TexFmt.RGBA32Sint,
            DXGI_FORMAT.R16G16B16A16_TYPELESS or DXGI_FORMAT.R16G16B16A16_FLOAT => TexFmt.RGBA16Float,
            DXGI_FORMAT.R16G16B16A16_UNORM => TexFmt.RGBA16Unorm,
            DXGI_FORMAT.R16G16B16A16_SNORM => TexFmt.RGBA16Snorm,
            DXGI_FORMAT.R16G16B16A16_UINT => TexFmt.RGBA16Uint,
            DXGI_FORMAT.R16G16B16A16_SINT => TexFmt.RGBA16Sint,
            DXGI_FORMAT.R32G32_TYPELESS or DXGI_FORMAT.R32G32_FLOAT => TexFmt.RG32Float,
            DXGI_FORMAT.R32G32_UINT => TexFmt.RG32Uint,
            DXGI_FORMAT.R32G32_SINT => TexFmt.RG32Sint,
            DXGI_FORMAT.R32G8X24_TYPELESS or DXGI_FORMAT.D32_FLOAT_S8X24_UINT or DXGI_FORMAT.R32_FLOAT_X8X24_TYPELESS => TexFmt.Depth32FloatStencil8,
            DXGI_FORMAT.R10G10B10A2_TYPELESS or DXGI_FORMAT.R10G10B10A2_UNORM => TexFmt.RGB10A2Unorm,
            DXGI_FORMAT.R10G10B10A2_UINT => TexFmt.RGB10A2Uint,
            DXGI_FORMAT.R11G11B10_FLOAT => TexFmt.RG11B10Ufloat,
            DXGI_FORMAT.R8G8B8A8_TYPELESS or DXGI_FORMAT.R8G8B8A8_UNORM => TexFmt.RGBA8Unorm,
            DXGI_FORMAT.R8G8B8A8_UNORM_SRGB => TexFmt.RGBA8UnormSrgb,
            DXGI_FORMAT.R8G8B8A8_UINT => TexFmt.RGBA8Uint,
            DXGI_FORMAT.R8G8B8A8_SNORM => TexFmt.RGBA8Snorm,
            DXGI_FORMAT.R8G8B8A8_SINT => TexFmt.RGBA8Sint,
            DXGI_FORMAT.R16G16_TYPELESS or DXGI_FORMAT.R16G16_FLOAT => TexFmt.RG16Float,
            DXGI_FORMAT.R16G16_UNORM => TexFmt.RG16Unorm,
            DXGI_FORMAT.R16G16_SNORM => TexFmt.RG16Snorm,
            DXGI_FORMAT.R16G16_UINT => TexFmt.RG16Uint,
            DXGI_FORMAT.R16G16_SINT => TexFmt.RG16Sint,
            DXGI_FORMAT.R32_TYPELESS or DXGI_FORMAT.R32_FLOAT => TexFmt.R32Float,
            DXGI_FORMAT.D32_FLOAT => TexFmt.Depth32Float,
            DXGI_FORMAT.R32_UINT => TexFmt.R32Uint,
            DXGI_FORMAT.R32_SINT => TexFmt.R32Sint,
            DXGI_FORMAT.R24G8_TYPELESS or DXGI_FORMAT.D24_UNORM_S8_UINT or DXGI_FORMAT.R24_UNORM_X8_TYPELESS => TexFmt.Depth24PlusStencil8,
            DXGI_FORMAT.R8G8_TYPELESS or DXGI_FORMAT.R8G8_UNORM => TexFmt.RG8Unorm,
            DXGI_FORMAT.R8G8_UINT => TexFmt.RG8Uint,
            DXGI_FORMAT.R8G8_SNORM => TexFmt.RG8Snorm,
            DXGI_FORMAT.R8G8_SINT => TexFmt.RG8Sint,
            DXGI_FORMAT.R16_TYPELESS or DXGI_FORMAT.R16_FLOAT => TexFmt.R16Float,
            DXGI_FORMAT.D16_UNORM => TexFmt.Depth16Unorm,
            DXGI_FORMAT.R16_UNORM => TexFmt.R16Unorm,
            DXGI_FORMAT.R16_UINT => TexFmt.R16Uint,
            DXGI_FORMAT.R16_SNORM => TexFmt.R16Snorm,
            DXGI_FORMAT.R16_SINT => TexFmt.R16Sint,
            DXGI_FORMAT.R8_TYPELESS or DXGI_FORMAT.R8_UNORM or DXGI_FORMAT.A8_UNORM => TexFmt.R8Unorm,
            DXGI_FORMAT.R8_UINT => TexFmt.R8Uint,
            DXGI_FORMAT.R8_SNORM => TexFmt.R8Snorm,
            DXGI_FORMAT.R8_SINT => TexFmt.R8Sint,
            DXGI_FORMAT.BC1_TYPELESS or DXGI_FORMAT.BC1_UNORM => TexFmt.BC1,
            DXGI_FORMAT.BC1_UNORM_SRGB => TexFmt.BC1Srgb,
            DXGI_FORMAT.BC2_TYPELESS or DXGI_FORMAT.BC2_UNORM => TexFmt.BC2,
            DXGI_FORMAT.BC2_UNORM_SRGB => TexFmt.BC2Srgb,
            DXGI_FORMAT.BC3_TYPELESS or DXGI_FORMAT.BC3_UNORM => TexFmt.BC3,
            DXGI_FORMAT.BC3_UNORM_SRGB => TexFmt.BC3Srgb,
            DXGI_FORMAT.BC4_TYPELESS or DXGI_FORMAT.BC4_UNORM => TexFmt.BC4,
            DXGI_FORMAT.BC4_SNORM => TexFmt.BC4Snorm,
            DXGI_FORMAT.BC5_TYPELESS or DXGI_FORMAT.BC5_UNORM => TexFmt.BC5,
            DXGI_FORMAT.BC5_SNORM => TexFmt.BC5Snorm,
            DXGI_FORMAT.BC6H_TYPELESS or DXGI_FORMAT.BC6H_UF16 => TexFmt.BC6HU,
            DXGI_FORMAT.BC6H_SF16 => TexFmt.BC6HS,
            DXGI_FORMAT.BC7_TYPELESS or DXGI_FORMAT.BC7_UNORM => TexFmt.BC7,
            DXGI_FORMAT.BC7_UNORM_SRGB => TexFmt.BC7Srgb,
            DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.B8G8R8A8_TYPELESS or DXGI_FORMAT.B8G8R8X8_UNORM or DXGI_FORMAT.B8G8R8X8_TYPELESS => TexFmt.BGRA8Unorm,
            DXGI_FORMAT.B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.B8G8R8X8_UNORM_SRGB => TexFmt.BGRA8UnormSrgb,
            // 4:2:0 video: one texture of the formats' bytes as they lie in memory -- the luma rows, then
            // the interleaved chroma rows, as many again as half -- read by the video processor and the
            // plane views (D3DVideoProcessor.cs)
            NV12 => TexFmt.R8Unorm,
            P010 or P016 => TexFmt.R16Uint,
            _ => WGPUTextureFormat.Undefined,
        };

        public const DXGI_FORMAT NV12 = (DXGI_FORMAT)103, P010 = (DXGI_FORMAT)104, P016 = (DXGI_FORMAT)105;

        /// <summary>A planar 4:2:0 format: a luma plane and a half-height plane of interleaved chroma.</summary>
        public static bool IsPlanar(DXGI_FORMAT f) => f is NV12 or P010 or P016;

        /// <summary>Bytes per texel (per 4x4 block for a block-compressed format).</summary>
        public static uint TexelBytes(DXGI_FORMAT f) => f switch
        {
            DXGI_FORMAT.R32G32B32A32_TYPELESS or DXGI_FORMAT.R32G32B32A32_FLOAT or DXGI_FORMAT.R32G32B32A32_UINT or DXGI_FORMAT.R32G32B32A32_SINT => 16,
            DXGI_FORMAT.R32G32B32_TYPELESS or DXGI_FORMAT.R32G32B32_FLOAT or DXGI_FORMAT.R32G32B32_UINT or DXGI_FORMAT.R32G32B32_SINT => 12,
            >= DXGI_FORMAT.R16G16B16A16_TYPELESS and <= DXGI_FORMAT.X32_TYPELESS_G8X24_UINT => 8,
            >= DXGI_FORMAT.R10G10B10A2_TYPELESS and <= DXGI_FORMAT.X24_TYPELESS_G8_UINT => 4,
            >= DXGI_FORMAT.R8G8_TYPELESS and <= DXGI_FORMAT.R16_SINT => 2,
            >= DXGI_FORMAT.R8_TYPELESS and <= DXGI_FORMAT.A8_UNORM => 1,
            DXGI_FORMAT.BC1_TYPELESS or DXGI_FORMAT.BC1_UNORM or DXGI_FORMAT.BC1_UNORM_SRGB or DXGI_FORMAT.BC4_TYPELESS or DXGI_FORMAT.BC4_UNORM or DXGI_FORMAT.BC4_SNORM => 8,
            >= DXGI_FORMAT.BC2_TYPELESS and <= DXGI_FORMAT.BC5_SNORM => 16,
            >= DXGI_FORMAT.BC6H_TYPELESS and <= DXGI_FORMAT.BC7_UNORM_SRGB => 16,
            >= DXGI_FORMAT.B8G8R8A8_UNORM and <= DXGI_FORMAT.B8G8R8X8_UNORM_SRGB => 4,
            NV12 => 1,
            P010 or P016 => 2,
            _ => 4,
        };

        public static bool IsBlockCompressed(DXGI_FORMAT f) => f is >= DXGI_FORMAT.BC1_TYPELESS and <= DXGI_FORMAT.BC5_SNORM or >= DXGI_FORMAT.BC6H_TYPELESS and <= DXGI_FORMAT.BC7_UNORM_SRGB;

        public static bool IsDepth(WGPUTextureFormat f) => f is TexFmt.Depth16Unorm or TexFmt.Depth24Plus or TexFmt.Depth24PlusStencil8 or TexFmt.Depth32Float or TexFmt.Depth32FloatStencil8 or TexFmt.Stencil8;

        public static bool HasStencil(WGPUTextureFormat f) => f is TexFmt.Depth24PlusStencil8 or TexFmt.Depth32FloatStencil8 or TexFmt.Stencil8;

        /// <summary>The vertex format of an input element of this DXGI format.</summary>
        public static WGPUVertexFormat Vertex(DXGI_FORMAT f) => f switch
        {
            DXGI_FORMAT.R32G32B32A32_FLOAT => VtxFmt.Float32x4,
            DXGI_FORMAT.R32G32B32_FLOAT => VtxFmt.Float32x3,
            DXGI_FORMAT.R32G32_FLOAT => VtxFmt.Float32x2,
            DXGI_FORMAT.R32_FLOAT => VtxFmt.Float32,
            DXGI_FORMAT.R32G32B32A32_UINT => VtxFmt.Uint32x4,
            DXGI_FORMAT.R32G32B32_UINT => VtxFmt.Uint32x3,
            DXGI_FORMAT.R32G32_UINT => VtxFmt.Uint32x2,
            DXGI_FORMAT.R32_UINT => VtxFmt.Uint32,
            DXGI_FORMAT.R32G32B32A32_SINT => VtxFmt.Sint32x4,
            DXGI_FORMAT.R32G32B32_SINT => VtxFmt.Sint32x3,
            DXGI_FORMAT.R32G32_SINT => VtxFmt.Sint32x2,
            DXGI_FORMAT.R32_SINT => VtxFmt.Sint32,
            DXGI_FORMAT.R16G16B16A16_FLOAT => VtxFmt.Float16x4,
            DXGI_FORMAT.R16G16_FLOAT => VtxFmt.Float16x2,
            DXGI_FORMAT.R16G16B16A16_UNORM => VtxFmt.Unorm16x4,
            DXGI_FORMAT.R16G16_UNORM => VtxFmt.Unorm16x2,
            DXGI_FORMAT.R16G16B16A16_SNORM => VtxFmt.Snorm16x4,
            DXGI_FORMAT.R16G16_SNORM => VtxFmt.Snorm16x2,
            DXGI_FORMAT.R16G16B16A16_UINT => VtxFmt.Uint16x4,
            DXGI_FORMAT.R16G16_UINT => VtxFmt.Uint16x2,
            DXGI_FORMAT.R16G16B16A16_SINT => VtxFmt.Sint16x4,
            DXGI_FORMAT.R16G16_SINT => VtxFmt.Sint16x2,
            DXGI_FORMAT.R8G8B8A8_UNORM => VtxFmt.Unorm8x4,
            DXGI_FORMAT.R8G8_UNORM => VtxFmt.Unorm8x2,
            DXGI_FORMAT.R8G8B8A8_SNORM => VtxFmt.Snorm8x4,
            DXGI_FORMAT.R8G8_SNORM => VtxFmt.Snorm8x2,
            DXGI_FORMAT.R8G8B8A8_UINT => VtxFmt.Uint8x4,
            DXGI_FORMAT.R8G8_UINT => VtxFmt.Uint8x2,
            DXGI_FORMAT.R8G8B8A8_SINT => VtxFmt.Sint8x4,
            DXGI_FORMAT.R8G8_SINT => VtxFmt.Sint8x2,
            // wgpu-native here has no unorm8x4-bgra (it aborts on it): read as RGBA, the vertex shader swaps
            DXGI_FORMAT.B8G8R8A8_UNORM => VtxFmt.Unorm8x4,
            DXGI_FORMAT.R10G10B10A2_UNORM => VtxFmt.Unorm10_10_10_2,
            DXGI_FORMAT.R8_UINT => VtxFmt.Uint8, DXGI_FORMAT.R8_SINT => VtxFmt.Sint8, DXGI_FORMAT.R8_UNORM => VtxFmt.Unorm8, DXGI_FORMAT.R8_SNORM => VtxFmt.Snorm8,
            DXGI_FORMAT.R16_UINT => VtxFmt.Uint16, DXGI_FORMAT.R16_SINT => VtxFmt.Sint16, DXGI_FORMAT.R16_UNORM => VtxFmt.Unorm16, DXGI_FORMAT.R16_SNORM => VtxFmt.Snorm16,
            DXGI_FORMAT.R16_FLOAT => VtxFmt.Float16,
            _ => (WGPUVertexFormat)0,
        };
    }
}
