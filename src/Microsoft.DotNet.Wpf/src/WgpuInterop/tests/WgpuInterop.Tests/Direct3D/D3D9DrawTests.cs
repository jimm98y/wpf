// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Direct3D 9 drawing through the device's vtable, as an application draws into a D3DImage surface:
// vertex and pixel shaders (bytecode assembled here, token by token, as fxc lays it out), the
// fixed-function pipeline (pre-transformed vertices, transforms and lighting, texture stages), the
// output merger (depth, blending), the primitive types WebGPU lacks (fans, wireframe), Clear, state
// blocks -- each read back as D3DImage reads its surface.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3D9DrawTests
    {
        private static IntPtr Fn(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        private static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com);
        private static void Check(int hr, string what) => Assert.True(hr >= 0, $"{what} failed: 0x{hr:x8}");
        private static void RequireGpu() => Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");

        private const int W = 64, H = 64;
        private const int D3DFMT_A8R8G8B8 = 21, D3DFMT_X8R8G8B8 = 22, D3DFMT_D24S8 = 75;
        private const uint FVF_XYZ = 0x2, FVF_XYZRHW = 0x4, FVF_NORMAL = 0x10, FVF_DIFFUSE = 0x40, FVF_TEX1 = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        internal struct PresentParameters
        {
            public uint BackBufferWidth, BackBufferHeight;
            public int BackBufferFormat;
            public uint BackBufferCount;
            public int MultiSampleType;
            public uint MultiSampleQuality;
            public int SwapEffect;
            public IntPtr hDeviceWindow;
            public int Windowed, EnableAutoDepthStencil, AutoDepthStencilFormat;
            public uint Flags, FullScreen_RefreshRateInHz, PresentationInterval;
        }

        /// <summary>A device with a render target (and depth surface) bound: what a D3DImage sample sets up.</summary>
        private sealed class Scene : IDisposable
        {
            public IntPtr D3D, Dev, Rt, Ds;
            private readonly List<IntPtr> _owned = new();

            /// <summary>A device whose back buffer (and automatic depth-stencil surface) is what it draws to.</summary>
            public Scene(PresentParameters pp)
            {
                IntPtr d3d, dev;
                Check(Exports.Create9ExPointer(32, &d3d), "Direct3DCreate9Ex");
                Check(((delegate* unmanaged<IntPtr, uint, int, IntPtr, uint, PresentParameters*, void*, IntPtr*, int>)Fn(d3d, Vtbl.IDirect3D9Ex.CreateDeviceEx))(d3d, 0, 1, IntPtr.Zero, 0x40 | 0x4 | 0x2, &pp, null, &dev), "CreateDeviceEx");
                D3D = d3d; Dev = dev;
                IntPtr bb;
                Check(((delegate* unmanaged<IntPtr, uint, uint, int, IntPtr*, int>)Fn(dev, Vtbl.IDirect3DDevice9.GetBackBuffer))(dev, 0, 0, 0, &bb), "GetBackBuffer");
                Rt = bb;
            }

            public Scene(bool depth = false, int format = D3DFMT_A8R8G8B8)
            {
                IntPtr d3d, dev;
                Check(Exports.Create9ExPointer(32, &d3d), "Direct3DCreate9Ex");
                var pp = new PresentParameters { BackBufferWidth = 1, BackBufferHeight = 1, SwapEffect = 1, Windowed = 1, PresentationInterval = 0x80000000 };
                Check(((delegate* unmanaged<IntPtr, uint, int, IntPtr, uint, PresentParameters*, void*, IntPtr*, int>)Fn(d3d, Vtbl.IDirect3D9Ex.CreateDeviceEx))(d3d, 0, 1, IntPtr.Zero, 0x40 | 0x4 | 0x2, &pp, null, &dev), "CreateDeviceEx");
                D3D = d3d; Dev = dev;
                IntPtr rt;
                Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)Fn(dev, Vtbl.IDirect3DDevice9.CreateRenderTarget))(dev, W, H, format, 0, 0, 0, &rt, null), "CreateRenderTarget");
                Rt = rt;
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(dev, Vtbl.IDirect3DDevice9.SetRenderTarget))(dev, 0, rt), "SetRenderTarget");
                if (depth)
                {
                    IntPtr ds;
                    Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)Fn(dev, Vtbl.IDirect3DDevice9.CreateDepthStencilSurface))(dev, W, H, D3DFMT_D24S8, 0, 0, 1, &ds, null), "CreateDepthStencilSurface");
                    Ds = ds;
                    Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(dev, Vtbl.IDirect3DDevice9.SetDepthStencilSurface))(dev, ds), "SetDepthStencilSurface");
                }
            }

            public T Own<T>(T p) where T : unmanaged { _owned.Add(*(IntPtr*)&p); return p; }

            public void Dispose()
            {
                foreach (IntPtr p in _owned) Release(p);
                Release(Ds); Release(Rt); Release(Dev); Release(D3D);
            }

            public void Clear(uint flags, uint color, float z = 1, uint stencil = 0, D3DRECT9[]? rects = null)
            {
                if (rects == null) Check(((delegate* unmanaged<IntPtr, uint, D3DRECT9*, uint, uint, float, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.Clear))(Dev, 0, null, flags, color, z, stencil), "Clear");
                else fixed (D3DRECT9* r = rects) Check(((delegate* unmanaged<IntPtr, uint, D3DRECT9*, uint, uint, float, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.Clear))(Dev, (uint)rects.Length, r, flags, color, z, stencil), "Clear");
            }

            public void RS(int state, uint value) => Check(((delegate* unmanaged<IntPtr, int, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetRenderState))(Dev, state, value), $"SetRenderState {state}");
            public void Tss(uint stage, int type, uint value) => Check(((delegate* unmanaged<IntPtr, uint, int, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetTextureStageState))(Dev, stage, type, value), "SetTextureStageState");
            public void Samp(uint s, int type, uint value) => Check(((delegate* unmanaged<IntPtr, uint, int, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetSamplerState))(Dev, s, type, value), "SetSamplerState");
            public void Fvf(uint fvf) => Check(((delegate* unmanaged<IntPtr, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetFVF))(Dev, fvf), "SetFVF");
            public void Texture(uint s, IntPtr t) => Check(((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetTexture))(Dev, s, t), "SetTexture");

            public void Transform(int state, float[] m)
            {
                fixed (float* p = m) Check(((delegate* unmanaged<IntPtr, int, float*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetTransform))(Dev, state, p), "SetTransform");
            }

            public void DrawUp<T>(int type, uint prims, T[] vertices) where T : unmanaged
            {
                fixed (T* v = vertices)
                    Check(((delegate* unmanaged<IntPtr, int, uint, void*, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.DrawPrimitiveUP))(Dev, type, prims, v, (uint)sizeof(T)), "DrawPrimitiveUP");
            }

            public void BeginScene() => Check(((delegate* unmanaged<IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.BeginScene))(Dev), "BeginScene");
            public void EndScene() => Check(((delegate* unmanaged<IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.EndScene))(Dev), "EndScene");

            public IntPtr VertexShader(uint[] code)
            {
                IntPtr s;
                fixed (uint* c = code) Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.CreateVertexShader))(Dev, c, &s), "CreateVertexShader");
                Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetVertexShader))(Dev, s), "SetVertexShader");
                return Own(s);
            }

            public IntPtr PixelShader(uint[] code)
            {
                IntPtr s;
                fixed (uint* c = code) Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.CreatePixelShader))(Dev, c, &s), "CreatePixelShader");
                Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetPixelShader))(Dev, s), "SetPixelShader");
                return Own(s);
            }

            public void VsConst(uint reg, params float[] v)
            {
                fixed (float* p = v) Check(((delegate* unmanaged<IntPtr, uint, float*, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetVertexShaderConstantF))(Dev, reg, p, (uint)v.Length / 4), "SetVertexShaderConstantF");
            }

            public void PsConst(uint reg, params float[] v)
            {
                fixed (float* p = v) Check(((delegate* unmanaged<IntPtr, uint, float*, uint, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetPixelShaderConstantF))(Dev, reg, p, (uint)v.Length / 4), "SetPixelShaderConstantF");
            }

            public void Declaration(params D3DVERTEXELEMENT9[] elements)
            {
                var all = new D3DVERTEXELEMENT9[elements.Length + 1];
                elements.CopyTo(all, 0);
                all[^1] = new D3DVERTEXELEMENT9 { Stream = 0xFF, Type = 17 };
                IntPtr decl;
                fixed (D3DVERTEXELEMENT9* e = all) Check(((delegate* unmanaged<IntPtr, D3DVERTEXELEMENT9*, IntPtr*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.CreateVertexDeclaration))(Dev, e, &decl), "CreateVertexDeclaration");
                Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.SetVertexDeclaration))(Dev, decl), "SetVertexDeclaration");
                Own(decl);
            }

            /// <summary>A managed-pool texture of A8R8G8B8 texels.</summary>
            public IntPtr Texture(uint w, uint h, uint[] texels)
            {
                IntPtr t;
                Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.CreateTexture))(Dev, w, h, 1, 0, D3DFMT_A8R8G8B8, 1, &t, null), "CreateTexture");
                D3DLOCKED_RECT lr;
                Check(((delegate* unmanaged<IntPtr, uint, D3DLOCKED_RECT*, D3DRECT9*, uint, int>)Fn(t, Vtbl.IDirect3DTexture9.LockRect))(t, 0, &lr, null, 0), "LockRect");
                for (int y = 0; y < h; y++) new ReadOnlySpan<uint>(texels, (int)(y * w), (int)w).CopyTo(new Span<uint>((byte*)lr.pBits + y * lr.Pitch, (int)w));
                Check(((delegate* unmanaged<IntPtr, uint, int>)Fn(t, Vtbl.IDirect3DTexture9.UnlockRect))(t, 0), "UnlockRect");
                return Own(t);
            }

            /// <summary>The render target's pixels (A8R8G8B8), as D3DImage reads them.</summary>
            public uint[] Pixels()
            {
                IntPtr copy;
                Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(Dev, Vtbl.IDirect3DDevice9.CreateOffscreenPlainSurface))(Dev, W, H, D3DFMT_A8R8G8B8, 2, &copy, null), "CreateOffscreenPlainSurface");
                try
                {
                    Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Fn(Dev, Vtbl.IDirect3DDevice9.GetRenderTargetData))(Dev, Rt, copy), "GetRenderTargetData");
                    D3DLOCKED_RECT lr;
                    Check(((delegate* unmanaged<IntPtr, D3DLOCKED_RECT*, void*, uint, int>)Fn(copy, Vtbl.IDirect3DSurface9.LockRect))(copy, &lr, null, 0x10), "LockRect");
                    var px = new uint[W * H];
                    for (int y = 0; y < H; y++) new ReadOnlySpan<uint>((byte*)lr.pBits + y * lr.Pitch, W).CopyTo(px.AsSpan(y * W));
                    Check(((delegate* unmanaged<IntPtr, int>)Fn(copy, Vtbl.IDirect3DSurface9.UnlockRect))(copy), "UnlockRect");
                    return px;
                }
                finally { Release(copy); }
            }

            public int Issued => ((Direct3DDevice9)ComObjectOf(Dev)).DrawsIssued;
            public int Skipped => ((Direct3DDevice9)ComObjectOf(Dev)).DrawsSkipped;
        }

        private static object ComObjectOf(IntPtr p) => GCHandle.FromIntPtr(((IntPtr*)p)[1]).Target!;

        private static void Near(uint expected, uint actual, int tolerance = 2, string? what = null)
        {
            for (int sh = 0; sh < 32; sh += 8)
            {
                int e = (int)((expected >> sh) & 0xFF), a = (int)((actual >> sh) & 0xFF);
                Assert.True(Math.Abs(e - a) <= tolerance, $"{what ?? "pixel"}: expected 0x{expected:x8}, got 0x{actual:x8}");
            }
        }

        // ---- bytecode, as fxc writes it -----------------------------------------------------------

        private static class Asm
        {
            public const int Temp = 0, Input = 1, Const = 2, Addr = 3, Texture = 3, RastOut = 4, AttrOut = 5, TexCrdOut = 6, Output = 6, ConstInt = 7, ColorOut = 8, Sampler = 10, ConstBool = 14, Loop = 15;
            public static uint Reg(int type, int n) => 0x80000000u | (uint)n | ((uint)(type & 7) << 28) | ((uint)((type >> 3) & 3) << 11);
            public static uint D(int type, int n, int mask = 0xF, bool sat = false) => Reg(type, n) | ((uint)mask << 16) | (sat ? 1u << 20 : 0);
            public static uint S(int type, int n, int swizzle = 0xE4, int mod = 0) => Reg(type, n) | ((uint)swizzle << 16) | ((uint)mod << 24);
            public static uint Op(int op, int len, int control = 0) => (uint)op | ((uint)len << 24) | ((uint)control << 16);
            public static uint[] Dcl(int usage, int index, uint reg) => new[] { Op(31, 2), 0x80000000u | (uint)usage | ((uint)index << 16), reg };
            public static uint[] DclSampler2D(int n) => new[] { Op(31, 2), 0x80000000u | (2u << 27), D(Sampler, n) };
            public static uint[] Def(int reg, float x, float y, float z, float w) => new[] { Op(81, 5), D(Const, reg), F(x), F(y), F(z), F(w) };
            public static uint F(float f) => BitConverter.SingleToUInt32Bits(f);
            public static uint[] Code(uint version, params uint[][] parts)
            {
                var all = new List<uint> { version };
                foreach (var p in parts) all.AddRange(p);
                all.Add(0x0000FFFF);
                return all.ToArray();
            }
            public static uint[] I(params uint[] t) => t;
        }

        private static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        [StructLayout(LayoutKind.Sequential)] private struct PosColor { public float X, Y, Z; public uint Color; }
        [StructLayout(LayoutKind.Sequential)] private struct RhwColor { public float X, Y, Z, Rhw; public uint Color; }
        [StructLayout(LayoutKind.Sequential)] private struct RhwTex { public float X, Y, Z, Rhw, U, V; }
        [StructLayout(LayoutKind.Sequential)] private struct PosNormal { public float X, Y, Z, NX, NY, NZ; }

        // ---- shaders ------------------------------------------------------------------------------

        /// <summary>vs_2_0 that transforms by c0-c3 and passes the colour; ps_2_0 that scales it by c0.</summary>
        [Fact]
        public void ShaderModel2_TransformsByConstants_AndShadesByThem()
        {
            RequireGpu();
            using var s = new Scene();
            s.Declaration(
                new D3DVERTEXELEMENT9 { Stream = 0, Offset = 0, Type = 2, Usage = 0 },
                new D3DVERTEXELEMENT9 { Stream = 0, Offset = 12, Type = 4, Usage = 10 });
            s.VertexShader(Asm.Code(0xFFFE0200,
                Asm.Dcl(0, 0, Asm.D(Asm.Input, 0)),
                Asm.Dcl(10, 0, Asm.D(Asm.Input, 1)),
                Asm.I(Asm.Op(20, 3), Asm.D(Asm.RastOut, 0), Asm.S(Asm.Input, 0), Asm.S(Asm.Const, 0)),       // m4x4 oPos, v0, c0
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.AttrOut, 0), Asm.S(Asm.Input, 1))));                            // mov oD0, v1
            s.PixelShader(Asm.Code(0xFFFF0200,
                Asm.I(Asm.Op(31, 2), 0x80000000u, Asm.D(Asm.Input, 0)),                                     // dcl v0
                Asm.I(Asm.Op(5, 3), Asm.D(Asm.ColorOut, 0), Asm.S(Asm.Input, 0), Asm.S(Asm.Const, 0))));      // mul oC0, v0, c0
            // m4x4 reads the rows c0-c3: x scaled by 0.5 (the triangle in the left half)
            s.VsConst(0, 0.5f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            s.PsConst(0, 1, 0.5f, 1, 1);
            s.RS(D3DRS.CULLMODE, 1);
            s.Clear(1, 0xFF000000);
            s.BeginScene();
            s.DrawUp(4, 2, new[]
            {
                new PosColor { X = -1, Y = 1, Z = 0.5f, Color = 0xFF00FF00 }, new PosColor { X = 1, Y = 1, Z = 0.5f, Color = 0xFF00FF00 }, new PosColor { X = -1, Y = -1, Z = 0.5f, Color = 0xFF00FF00 },
                new PosColor { X = 1, Y = 1, Z = 0.5f, Color = 0xFF00FF00 }, new PosColor { X = 1, Y = -1, Z = 0.5f, Color = 0xFF00FF00 }, new PosColor { X = -1, Y = -1, Z = 0.5f, Color = 0xFF00FF00 },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(1, s.Issued);
            Near(0xFF008000, px[32 * W + 20], 2, "inside (green halved)");
            Near(0xFF008000, px[32 * W + 44], 2, "inside right of the middle");
            Assert.Equal(0xFF000000u, px[32 * W + 10]);
            Assert.Equal(0xFF000000u, px[32 * W + 50]);
        }

        /// <summary>ps_2_0 texld of the texture coordinates a vs_2_0 writes, with a def constant.</summary>
        [Fact]
        public void ShaderModel2_SamplesATexture()
        {
            RequireGpu();
            using var s = new Scene();
            s.Declaration(
                new D3DVERTEXELEMENT9 { Stream = 0, Offset = 0, Type = 2, Usage = 0 },
                new D3DVERTEXELEMENT9 { Stream = 0, Offset = 12, Type = 1, Usage = 5 });
            s.VertexShader(Asm.Code(0xFFFE0200,
                Asm.Dcl(0, 0, Asm.D(Asm.Input, 0)),
                Asm.Dcl(5, 0, Asm.D(Asm.Input, 1)),
                Asm.Def(4, 0, 0, 0, 1),
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.RastOut, 0, 0x7), Asm.S(Asm.Input, 0)),       // mov oPos.xyz, v0
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.RastOut, 0, 0x8), Asm.S(Asm.Const, 4)),       // mov oPos.w, c4
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.TexCrdOut, 0), Asm.S(Asm.Input, 1))));        // mov oT0, v1
            s.PixelShader(Asm.Code(0xFFFF0200,
                Asm.I(Asm.Op(31, 2), 0x80000000u, Asm.D(Asm.Texture, 0, 0x3)),              // dcl t0.xy
                Asm.DclSampler2D(0),
                Asm.I(Asm.Op(66, 3), Asm.D(Asm.Temp, 0), Asm.S(Asm.Texture, 0), Asm.S(Asm.Sampler, 0)),   // texld r0, t0, s0
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.ColorOut, 0), Asm.S(Asm.Temp, 0))));
            IntPtr tex = s.Texture(2, 2, new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF });
            s.Texture(0, tex);
            s.RS(D3DRS.CULLMODE, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, new[]
            {
                new PosTex(-1, 1, 0, 0), new PosTex(1, 1, 1, 0), new PosTex(-1, -1, 0, 1), new PosTex(1, -1, 1, 1),
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[8 * W + 8]);
            Assert.Equal(0xFF00FF00u, px[8 * W + 56]);
            Assert.Equal(0xFF0000FFu, px[56 * W + 8]);
            Assert.Equal(0xFFFFFFFFu, px[56 * W + 56]);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PosTex
        {
            public float X, Y, Z, U, V;
            public PosTex(float x, float y, float u, float v) { X = x; Y = y; Z = 0.5f; U = u; V = v; }
        }

        /// <summary>vs_3_0 with a loop over aL reading constants relative to it, and a static branch on a
        /// boolean constant the application sets.</summary>
        [Fact]
        public void ShaderModel3_LoopsAndBranchesOnConstants()
        {
            RequireGpu();
            using var s = new Scene();
            s.Declaration(new D3DVERTEXELEMENT9 { Stream = 0, Offset = 0, Type = 2, Usage = 0 });
            uint aL = 0x80000000u | (15u & 7) << 28 | ((15u >> 3) & 3) << 11;        // the aL register, x swizzle
            s.VertexShader(Asm.Code(0xFFFE0300,
                Asm.Dcl(0, 0, Asm.D(Asm.Input, 0)),
                Asm.Dcl(0, 0, Asm.D(Asm.Output, 0)),                                     // dcl_position o0
                Asm.Dcl(10, 0, Asm.D(Asm.Output, 1)),                                    // dcl_color o1
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.Output, 0, 0x7), Asm.S(Asm.Input, 0)),
                Asm.Def(20, 0, 0, 0, 1),
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.Output, 0, 0x8), Asm.S(Asm.Const, 20)),
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.Output, 1), Asm.S(Asm.Const, 21)),           // o1 = c21 (zero)
                // loop aL, i0 : o1 += c[aL] (c0, c1, c2: 0.25 red each)
                Asm.I(Asm.Op(27, 2), aL, Asm.S(Asm.ConstInt, 0)),
                Asm.I(Asm.Op(2, 4), Asm.D(Asm.Output, 1), Asm.S(Asm.Output, 1), Asm.S(Asm.Const, 0) | (1u << 13), aL),
                Asm.I(Asm.Op(29, 0)),
                // if b0: o1.g = 1
                Asm.I(Asm.Op(40, 1), Asm.S(Asm.ConstBool, 0)),
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.Output, 1, 0x2), Asm.S(Asm.Const, 20, 0xFF)),
                Asm.I(Asm.Op(43, 0))));
            s.PixelShader(Asm.Code(0xFFFF0300,
                Asm.Dcl(10, 0, Asm.D(Asm.Input, 0)),
                Asm.I(Asm.Op(1, 2), Asm.D(Asm.ColorOut, 0), Asm.S(Asm.Input, 0))));
            s.VsConst(0, 0.25f, 0, 0, 1, 0.25f, 0, 0, 0, 0.25f, 0, 0, 0);
            int* ints = stackalloc int[4] { 3, 0, 1, 0 };
            Check(((delegate* unmanaged<IntPtr, uint, int*, uint, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetVertexShaderConstantI))(s.Dev, 0, ints, 1), "SetVertexShaderConstantI");
            int on = 1;
            Check(((delegate* unmanaged<IntPtr, uint, int*, uint, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetVertexShaderConstantB))(s.Dev, 0, &on, 1), "SetVertexShaderConstantB");
            s.RS(D3DRS.CULLMODE, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(4, 1, new[] { new Vec3(-1, 1), new Vec3(3, 1), new Vec3(-1, -3) });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0, s.Skipped);
            Near(0xFFBFFF00, px[32 * W + 32], 2, "0.75 red from the loop, green from the branch");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Vec3 { public float X, Y, Z; public Vec3(float x, float y) { X = x; Y = y; Z = 0.5f; } }

        // ---- the fixed-function pipeline -------------------------------------------------------------

        /// <summary>Pre-transformed vertices (XYZRHW) in pixels: D3D9's pixel centres are on integer
        /// coordinates, so a quad from 0 to 32 covers pixels 0-31 exactly.</summary>
        [Fact]
        public void PreTransformedVertices_CoverThePixelsTheyName()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, new[]
            {
                new RhwColor { X = 0, Y = 0, Z = 0.5f, Rhw = 1, Color = 0xFF0000FF }, new RhwColor { X = 32, Y = 0, Z = 0.5f, Rhw = 1, Color = 0xFF0000FF },
                new RhwColor { X = 0, Y = 32, Z = 0.5f, Rhw = 1, Color = 0xFF0000FF }, new RhwColor { X = 32, Y = 32, Z = 0.5f, Rhw = 1, Color = 0xFF0000FF },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF0000FFu, px[0]);
            Assert.Equal(0xFF0000FFu, px[31 * W + 31]);
            Assert.Equal(0xFF000000u, px[31 * W + 32]);
            Assert.Equal(0xFF000000u, px[32 * W + 31]);
        }

        /// <summary>A lit triangle: a directional light on a material, through world, view and projection.</summary>
        [Fact]
        public void FixedFunctionLighting_LightsTheMaterial()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZ | FVF_NORMAL);
            s.Transform(256, new float[] { 0.5f, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 1, 0, 0, 0, 0.5f, 1 });     // world: half size, z 0.5
            s.Transform(2, Identity);
            s.Transform(3, Identity);
            var mat = new D3DMATERIAL9 { Diffuse = new D3DCOLORVALUE { r = 1, g = 0.5f, b = 0, a = 1 }, Ambient = new D3DCOLORVALUE { b = 1, a = 1 } };
            Check(((delegate* unmanaged<IntPtr, D3DMATERIAL9*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetMaterial))(s.Dev, &mat), "SetMaterial");
            var light = new D3DLIGHT9 { Type = 3, Diffuse = new D3DCOLORVALUE { r = 1, g = 1, b = 1, a = 1 }, DirZ = 1 };
            Check(((delegate* unmanaged<IntPtr, uint, D3DLIGHT9*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetLight))(s.Dev, 0, &light), "SetLight");
            Check(((delegate* unmanaged<IntPtr, uint, int, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.LightEnable))(s.Dev, 0, 1), "LightEnable");
            s.RS(D3DRS.AMBIENT, 0xFF404040);
            s.RS(D3DRS.NORMALIZENORMALS, 1);
            s.Clear(1, 0xFF000000);
            // clockwise on screen (D3D9's front face, kept by the default CCW culling)
            s.DrawUp(4, 1, new[]
            {
                new PosNormal { X = -1, Y = 1, NZ = -1 }, new PosNormal { X = 1, Y = 1, NZ = -1 }, new PosNormal { X = -1, Y = -1, NZ = -1 },
            });
            // the same triangle counter-clockwise: culled
            s.DrawUp(4, 1, new[]
            {
                new PosNormal { X = 1, Y = 1, NZ = -1 }, new PosNormal { X = -1, Y = -1, NZ = -1 }, new PosNormal { X = 1, Y = -1, NZ = -1 },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            // diffuse (1, 0.5, 0) from N.L = 1, plus the material's blue ambient times the global 0x40
            Near(0xFFFF8040, px[26 * W + 26], 2, "lit");
            Assert.Equal(0xFF000000u, px[40 * W + 40]);
            Assert.Equal(0xFF000000u, px[4 * W + 4]);
        }

        /// <summary>Texture stage 0 modulating a texture with the vertex colour, and a point-sampled texture
        /// of four texels mapped onto the quad.</summary>
        [Fact]
        public void TextureStages_ModulateTheTextureByTheDiffuse()
        {
            RequireGpu();
            using var s = new Scene();
            IntPtr tex = s.Texture(2, 2, new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF });
            s.Texture(0, tex);
            s.Fvf(FVF_XYZRHW | FVF_TEX1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, new[]
            {
                new RhwTex { X = -0.5f, Y = -0.5f, Z = 0.5f, Rhw = 1, U = 0, V = 0 }, new RhwTex { X = 63.5f, Y = -0.5f, Z = 0.5f, Rhw = 1, U = 1, V = 0 },
                new RhwTex { X = -0.5f, Y = 63.5f, Z = 0.5f, Rhw = 1, U = 0, V = 1 }, new RhwTex { X = 63.5f, Y = 63.5f, Z = 0.5f, Rhw = 1, U = 1, V = 1 },
            });
            // TFACTOR as the second argument: the texture halved
            s.RS(D3DRS.TEXTUREFACTOR, 0xFF808080);
            s.Tss(0, 3, 3);       // COLORARG2 = TFACTOR
            var rect = new RhwTex[]
            {
                new() { X = 31.5f, Y = 31.5f, Z = 0.5f, Rhw = 1, U = 0.5f, V = 0.5f }, new() { X = 63.5f, Y = 31.5f, Z = 0.5f, Rhw = 1, U = 1, V = 0.5f },
                new() { X = 31.5f, Y = 63.5f, Z = 0.5f, Rhw = 1, U = 0.5f, V = 1 }, new() { X = 63.5f, Y = 63.5f, Z = 0.5f, Rhw = 1, U = 1, V = 1 },
            };
            s.DrawUp(5, 2, rect);
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[8 * W + 8]);
            Assert.Equal(0xFF00FF00u, px[8 * W + 56]);
            Assert.Equal(0xFF0000FFu, px[56 * W + 8]);
            Near(0xFF808080, px[56 * W + 56], 1, "white texel times the 0x80 factor");
        }

        /// <summary>The depth test against a depth surface Clear set, and blending by the source alpha.</summary>
        [Fact]
        public void DepthAndBlending_FollowTheRenderStates()
        {
            RequireGpu();
            using var s = new Scene(depth: true);
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.RS(D3DRS.ZENABLE, 1);
            s.Clear(1 | 2, 0xFF000000, 0.5f);
            RhwColor[] Quad(float x0, float x1, float z, uint c) => new[]
            {
                new RhwColor { X = x0, Y = 0, Z = z, Rhw = 1, Color = c }, new RhwColor { X = x1, Y = 0, Z = z, Rhw = 1, Color = c },
                new RhwColor { X = x0, Y = 64, Z = z, Rhw = 1, Color = c }, new RhwColor { X = x1, Y = 64, Z = z, Rhw = 1, Color = c },
            };
            s.DrawUp(5, 2, Quad(0, 32, 0.7f, 0xFFFF0000));     // behind the cleared depth: not drawn
            s.DrawUp(5, 2, Quad(32, 64, 0.3f, 0xFF00FF00));    // in front: drawn
            s.RS(D3DRS.ZENABLE, 0);
            s.Tss(0, 5, 0);        // ALPHAARG1 = DIFFUSE: the vertex's alpha
            s.RS(D3DRS.ALPHABLENDENABLE, 1);
            s.RS(D3DRS.SRCBLEND, 5);
            s.RS(D3DRS.DESTBLEND, 6);
            s.DrawUp(5, 2, Quad(48, 64, 0.1f, 0x800000FF));    // half blue over the green
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF000000u, px[10 * W + 10]);
            Assert.Equal(0xFF00FF00u, px[10 * W + 40]);
            Near(0xBF007F80, px[10 * W + 56], 2, "blended (alpha too: 0.5 * 0.5 + 1 * 0.5)");
        }

        /// <summary>A fan from vertex and index buffers (WebGPU has no fans), and a cleared rectangle.</summary>
        [Fact]
        public void IndexedFans_AndClearRectangles()
        {
            RequireGpu();
            using var s = new Scene();
            IntPtr vb, ib;
            Check(((delegate* unmanaged<IntPtr, uint, uint, uint, int, IntPtr*, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateVertexBuffer))(s.Dev, (uint)(5 * sizeof(RhwColor)), 0x8, FVF_XYZRHW | FVF_DIFFUSE, 1, &vb, null), "CreateVertexBuffer");
            s.Own(vb);
            Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateIndexBuffer))(s.Dev, 5 * 2, 0x8, 101, 1, &ib, null), "CreateIndexBuffer");
            s.Own(ib);
            void* data;
            Check(((delegate* unmanaged<IntPtr, uint, uint, void**, uint, int>)Fn(vb, Vtbl.IDirect3DVertexBuffer9.Lock))(vb, 0, 0, &data, 0), "Lock");
            var v = (RhwColor*)data;
            v[0] = new RhwColor { X = 32, Y = 32, Z = 0.5f, Rhw = 1, Color = 0xFFFFFF00 };      // the centre
            v[1] = new RhwColor { X = 8, Y = 8, Z = 0.5f, Rhw = 1, Color = 0xFFFFFF00 };
            v[2] = new RhwColor { X = 56, Y = 8, Z = 0.5f, Rhw = 1, Color = 0xFFFFFF00 };
            v[3] = new RhwColor { X = 56, Y = 56, Z = 0.5f, Rhw = 1, Color = 0xFFFFFF00 };
            v[4] = new RhwColor { X = 8, Y = 56, Z = 0.5f, Rhw = 1, Color = 0xFFFFFF00 };
            Check(((delegate* unmanaged<IntPtr, int>)Fn(vb, Vtbl.IDirect3DVertexBuffer9.Unlock))(vb), "Unlock");
            Check(((delegate* unmanaged<IntPtr, uint, uint, void**, uint, int>)Fn(ib, Vtbl.IDirect3DIndexBuffer9.Lock))(ib, 0, 0, &data, 0), "Lock");
            var idx = (ushort*)data;
            idx[0] = 0; idx[1] = 1; idx[2] = 2; idx[3] = 3; idx[4] = 4;
            Check(((delegate* unmanaged<IntPtr, int>)Fn(ib, Vtbl.IDirect3DIndexBuffer9.Unlock))(ib), "Unlock");
            Check(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, uint, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetStreamSource))(s.Dev, 0, vb, 0, (uint)sizeof(RhwColor)), "SetStreamSource");
            Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetIndices))(s.Dev, ib), "SetIndices");
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.RS(D3DRS.CULLMODE, 1);
            s.Clear(1, 0xFF000000);
            s.Clear(1, 0xFFFF00FF, rects: new[] { new D3DRECT9 { left = 0, top = 0, right = 4, bottom = 4 } });
            Check(((delegate* unmanaged<IntPtr, int, int, uint, uint, uint, uint, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.DrawIndexedPrimitive))(s.Dev, 6, 0, 0, 5, 0, 3), "DrawIndexedPrimitive");
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF00FFu, px[0]);
            Assert.Equal(0xFF000000u, px[4 * W + 4]);
            Assert.Equal(0xFFFFFF00u, px[20 * W + 32]);     // the fan's three triangles
            Assert.Equal(0xFFFFFF00u, px[50 * W + 50]);
            Assert.Equal(0xFF000000u, px[50 * W + 12]);     // the fourth triangle the fan does not have
        }

        /// <summary>Wireframe fill draws the triangles' edges, not their insides.</summary>
        [Fact]
        public void WireframeFill_DrawsTheEdges()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.RS(D3DRS.FILLMODE, 2);
            s.RS(D3DRS.CULLMODE, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(4, 1, new[]
            {
                new RhwColor { X = 8, Y = 8, Z = 0.5f, Rhw = 1, Color = 0xFFFFFFFF }, new RhwColor { X = 56, Y = 8, Z = 0.5f, Rhw = 1, Color = 0xFFFFFFFF },
                new RhwColor { X = 8, Y = 56, Z = 0.5f, Rhw = 1, Color = 0xFFFFFFFF },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFFFFFFu, px[8 * W + 30]);          // the top edge
            Assert.Equal(0xFF000000u, px[20 * W + 20]);         // inside
        }

        /// <summary>A state block captures the render states and restores them when applied.</summary>
        [Fact]
        public void StateBlocks_CaptureAndApply()
        {
            RequireGpu();
            using var s = new Scene();
            Check(((delegate* unmanaged<IntPtr, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.BeginStateBlock))(s.Dev), "BeginStateBlock");
            s.RS(D3DRS.CULLMODE, 1);
            s.RS(D3DRS.TEXTUREFACTOR, 0xFF123456);
            IntPtr block;
            Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.EndStateBlock))(s.Dev, &block), "EndStateBlock");
            s.Own(block);
            s.RS(D3DRS.CULLMODE, 3);
            s.RS(D3DRS.TEXTUREFACTOR, 0);
            s.RS(D3DRS.ZFUNC, 8);
            Check(((delegate* unmanaged<IntPtr, int>)Fn(block, Vtbl.IDirect3DStateBlock9.Apply))(block), "Apply");
            uint v;
            Check(((delegate* unmanaged<IntPtr, int, uint*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.GetRenderState))(s.Dev, D3DRS.CULLMODE, &v), "GetRenderState");
            Assert.Equal(1u, v);
            Check(((delegate* unmanaged<IntPtr, int, uint*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.GetRenderState))(s.Dev, D3DRS.TEXTUREFACTOR, &v), "GetRenderState");
            Assert.Equal(0xFF123456u, v);
            Check(((delegate* unmanaged<IntPtr, int, uint*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.GetRenderState))(s.Dev, D3DRS.ZFUNC, &v), "GetRenderState");
            Assert.Equal(8u, v);     // not in the block
        }

        /// <summary>The device's back buffer, render target 0 from the start, with the depth-stencil surface
        /// EnableAutoDepthStencil makes (and the depth test on): the SlimDX D3DImage sample's set-up.</summary>
        [Fact]
        public void TheBackBuffer_AndTheAutomaticDepthSurface_AreDrawnTo()
        {
            RequireGpu();
            using var s = new Scene(new PresentParameters
            {
                BackBufferWidth = W, BackBufferHeight = H, BackBufferFormat = D3DFMT_A8R8G8B8, BackBufferCount = 1, SwapEffect = 1, Windowed = 1,
                EnableAutoDepthStencil = 1, AutoDepthStencilFormat = D3DFMT_D24S8, PresentationInterval = 0x80000000,
            });
            IntPtr rt0;
            Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.GetRenderTarget))(s.Dev, 0, &rt0), "GetRenderTarget");
            Release(rt0);
            Assert.Equal(s.Rt, rt0);
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.Clear(1 | 2, 0xFF000000, 1);
            RhwColor[] Quad(float z, uint c) => new[]
            {
                new RhwColor { X = 0, Y = 0, Z = z, Rhw = 1, Color = c }, new RhwColor { X = 64, Y = 0, Z = z, Rhw = 1, Color = c },
                new RhwColor { X = 0, Y = 64, Z = z, Rhw = 1, Color = c }, new RhwColor { X = 64, Y = 64, Z = z, Rhw = 1, Color = c },
            };
            s.DrawUp(5, 2, Quad(0.3f, 0xFF00FF00));
            s.DrawUp(5, 2, Quad(0.6f, 0xFFFF0000));     // behind the first: the depth test (on by default) keeps it out
            s.EndScene();
            Check(((delegate* unmanaged<IntPtr, void*, void*, IntPtr, void*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.Present))(s.Dev, null, null, IntPtr.Zero, null), "Present");
            Assert.Equal(0xFF00FF00u, s.Pixels()[32 * W + 32]);
        }

        /// <summary>A user clip plane in world space (fixed function) keeps only the side it faces.</summary>
        [Fact]
        public void UserClipPlanes_CutTheGeometry()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZ | FVF_DIFFUSE);
            s.RS(D3DRS.LIGHTING, 0);
            s.RS(D3DRS.CULLMODE, 1);
            s.Transform(256, new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0.25f, 0, 0, 1 });    // world: moved right by 0.25
            float* plane = stackalloc float[4] { 1, 0, 0, -0.5f };       // x >= 0.5 in world space
            Check(((delegate* unmanaged<IntPtr, uint, float*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.SetClipPlane))(s.Dev, 0, plane), "SetClipPlane");
            s.RS(D3DRS.CLIPPLANEENABLE, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(4, 1, new[]
            {
                new PosColor { X = -2, Y = 1, Z = 0.5f, Color = 0xFFFFFFFF }, new PosColor { X = 2, Y = 1, Z = 0.5f, Color = 0xFFFFFFFF }, new PosColor { X = -2, Y = -3, Z = 0.5f, Color = 0xFFFFFFFF },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF000000u, px[4 * W + 44]);      // x 0.39: cut
            Assert.Equal(0xFFFFFFFFu, px[4 * W + 52]);      // x 0.64: kept
        }

        [StructLayout(LayoutKind.Sequential)] private struct RhwDir { public float X, Y, Z, Rhw, U, V, W; }

        /// <summary>A cube texture: each face filled through LockRect, sampled by direction.</summary>
        [Fact]
        public void CubeTextures_AreSampledByDirection()
        {
            RequireGpu();
            using var s = new Scene();
            IntPtr cube;
            Check(((delegate* unmanaged<IntPtr, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateCubeTexture))(s.Dev, 4, 1, 0, D3DFMT_A8R8G8B8, 1, &cube, null), "CreateCubeTexture");
            s.Own(cube);
            uint[] colors = { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00, 0xFFFF00FF, 0xFF00FFFF };
            for (int f = 0; f < 6; f++)
            {
                D3DLOCKED_RECT lr;
                Check(((delegate* unmanaged<IntPtr, int, uint, D3DLOCKED_RECT*, D3DRECT9*, uint, int>)Fn(cube, Vtbl.IDirect3DCubeTexture9.LockRect))(cube, f, 0, &lr, null, 0), "LockRect");
                for (int y = 0; y < 4; y++) new Span<uint>((byte*)lr.pBits + y * lr.Pitch, 4).Fill(colors[f]);
                Check(((delegate* unmanaged<IntPtr, int, uint, int>)Fn(cube, Vtbl.IDirect3DCubeTexture9.UnlockRect))(cube, f, 0), "UnlockRect");
            }
            s.Texture(0, cube);
            s.Fvf(FVF_XYZRHW | FVF_TEX1 | 0x10000);          // one set of three coordinates
            s.Clear(1, 0xFF000000);
            RhwDir[] Quad(float x0, float x1, float u, float v, float w) => new[]
            {
                new RhwDir { X = x0, Y = 0, Z = 0.5f, Rhw = 1, U = u, V = v, W = w }, new RhwDir { X = x1, Y = 0, Z = 0.5f, Rhw = 1, U = u, V = v, W = w },
                new RhwDir { X = x0, Y = 64, Z = 0.5f, Rhw = 1, U = u, V = v, W = w }, new RhwDir { X = x1, Y = 64, Z = 0.5f, Rhw = 1, U = u, V = v, W = w },
            };
            s.DrawUp(5, 2, Quad(0, 32, 1, 0.1f, 0.1f));       // +x
            s.DrawUp(5, 2, Quad(32, 64, 0.1f, 0.1f, -1));     // -z
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(colors[0], px[32 * W + 16]);
            Assert.Equal(colors[5], px[32 * W + 48]);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RhwSizeColor { public float X, Y, Z, Rhw, Size; public uint Color; }

        /// <summary>Points with D3DRS_POINTSIZE: a square of that many pixels around each, in the vertex's colour.</summary>
        [Fact]
        public void PointSize_DrawsSquares()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZRHW | FVF_DIFFUSE);
            s.Clear(1, 0xFF000000);
            s.RS(D3DRS.POINTSIZE, Asm.F(8));
            s.DrawUp(1, 2, new[]
            {
                new RhwColor { X = 16, Y = 16, Z = 0.5f, Rhw = 1, Color = 0xFFFF0000 }, new RhwColor { X = 48, Y = 40, Z = 0.5f, Rhw = 1, Color = 0xFF00FF00 },
            });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[13 * W + 13]);
            Assert.Equal(0xFFFF0000u, px[18 * W + 18]);
            Assert.Equal(0xFF000000u, px[16 * W + 10]);
            Assert.Equal(0xFF000000u, px[22 * W + 16]);
            Assert.Equal(0xFF00FF00u, px[40 * W + 48]);
            Assert.Equal(0xFF000000u, px[40 * W + 54]);
            Assert.Equal(0, s.Skipped);
        }

        /// <summary>A point sprite: the texture across the square, v down, whatever coordinates the vertex has.</summary>
        [Fact]
        public void PointSprites_SpanTheTexture()
        {
            RequireGpu();
            using var s = new Scene();
            IntPtr tex = s.Texture(2, 2, new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF });
            s.Texture(0, tex);
            s.Fvf(FVF_XYZRHW | FVF_TEX1);
            s.Clear(1, 0xFF000000);
            s.RS(D3DRS.POINTSPRITEENABLE, 1);
            s.RS(D3DRS.POINTSIZE, Asm.F(32));
            s.DrawUp(1, 1, new[] { new RhwTex { X = 32, Y = 32, Z = 0.5f, Rhw = 1, U = 0.9f, V = 0.9f } });
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[20 * W + 20]);
            Assert.Equal(0xFF00FF00u, px[20 * W + 44]);
            Assert.Equal(0xFF0000FFu, px[44 * W + 20]);
            Assert.Equal(0xFFFFFFFFu, px[44 * W + 44]);
            Assert.Equal(0xFF000000u, px[10 * W + 10]);
        }

        /// <summary>An indexed point list whose vertices carry their size (FVF PSIZE), clamped by POINTSIZE_MAX.</summary>
        [Fact]
        public void IndexedPoints_TakeTheVertexSize()
        {
            RequireGpu();
            using var s = new Scene();
            s.Fvf(FVF_XYZRHW | 0x20 | FVF_DIFFUSE);
            s.Clear(1, 0xFF000000);
            s.RS(D3DRS.POINTSIZE_MAX, Asm.F(10));
            var v = new[]
            {
                new RhwSizeColor { X = 16, Y = 16, Z = 0.5f, Rhw = 1, Size = 4, Color = 0xFFFF0000 },
                new RhwSizeColor { X = 48, Y = 48, Z = 0.5f, Rhw = 1, Size = 40, Color = 0xFF0000FF },
            };
            ushort[] idx = { 1, 0 };
            fixed (RhwSizeColor* pv = v)
            fixed (ushort* pi = idx)
                Check(((delegate* unmanaged<IntPtr, int, uint, uint, uint, void*, int, void*, uint, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.DrawIndexedPrimitiveUP))(s.Dev, 1, 0, 2, 2, pi, 101, pv, (uint)sizeof(RhwSizeColor)), "DrawIndexedPrimitiveUP");
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[16 * W + 17]);
            Assert.Equal(0xFF000000u, px[16 * W + 20]);
            Assert.Equal(0xFF0000FFu, px[48 * W + 52]);
            Assert.Equal(0xFF000000u, px[48 * W + 55]);        // 10, not 40
            Assert.Equal(0, s.Skipped);
        }

        [StructLayout(LayoutKind.Sequential)] private struct PosWeightColor { public float X, Y, Z, W0; public uint Color; }
        [StructLayout(LayoutKind.Sequential)] private struct PosWeightIndexColor { public float X, Y, Z, W0; public uint Indices, Color; }
        [StructLayout(LayoutKind.Sequential)] private struct PosPosColor { public float X, Y, Z, X1, Y1, Z1; public uint Color; }

        private static float[] TranslateX(float x) => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, 0, 0, 1 };

        /// <summary>A strip from x -1 to -0.5 (the left 16 pixels), all of y.</summary>
        private static T[] LeftStrip<T>(Func<float, float, T> v) => new[] { v(-1, 1), v(-0.5f, 1), v(-1, -1), v(-0.5f, -1) };

        /// <summary>Vertex blending: half of WORLD, half of WORLD1 (one unit right) moves the strip half a unit.</summary>
        [Fact]
        public void VertexBlending_WeighsTheWorldMatrices()
        {
            RequireGpu();
            using var s = new Scene();
            s.RS(D3DRS.LIGHTING, 0);
            s.Fvf(0x6 | FVF_DIFFUSE);       // XYZB1
            s.Transform(256, Identity);
            s.Transform(257, TranslateX(1));
            s.RS(D3DRS.VERTEXBLEND, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, LeftStrip((x, y) => new PosWeightColor { X = x, Y = y, Z = 0.5f, W0 = 0.5f, Color = 0xFFFF0000 }));
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF000000u, px[32 * W + 8]);
            Assert.Equal(0xFFFF0000u, px[32 * W + 24]);
            Assert.Equal(0xFF000000u, px[32 * W + 40]);
        }

        /// <summary>Indexed vertex blending: the vertex names its matrices (3: one unit right, 5: none).</summary>
        [Fact]
        public void IndexedVertexBlending_UsesTheVertexIndices()
        {
            RequireGpu();
            using var s = new Scene();
            s.RS(D3DRS.LIGHTING, 0);
            s.Fvf(0x8 | 0x1000 | FVF_DIFFUSE);     // XYZB2, the last beta UBYTE4 indices
            s.Transform(256 + 3, TranslateX(1));
            s.Transform(256 + 5, Identity);
            s.RS(D3DRS.VERTEXBLEND, 1);
            s.RS(D3DRS.INDEXEDVERTEXBLENDENABLE, 1);
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, LeftStrip((x, y) => new PosWeightIndexColor { X = x, Y = y, Z = 0.5f, W0 = 1, Indices = 0x0503, Color = 0xFF00FF00 }));
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF000000u, px[32 * W + 8]);
            Assert.Equal(0xFF00FF00u, px[32 * W + 40]);
        }

        /// <summary>Tweening: D3DRS_TWEENFACTOR between POSITION and POSITION1.</summary>
        [Fact]
        public void Tweening_MixesThePositions()
        {
            RequireGpu();
            using var s = new Scene();
            s.RS(D3DRS.LIGHTING, 0);
            s.Declaration(
                new D3DVERTEXELEMENT9 { Offset = 0, Type = 2, Usage = 0 },
                new D3DVERTEXELEMENT9 { Offset = 12, Type = 2, Usage = 0, UsageIndex = 1 },
                new D3DVERTEXELEMENT9 { Offset = 24, Type = 4, Usage = 10 });
            s.RS(D3DRS.VERTEXBLEND, 255);
            s.RS(D3DRS.TWEENFACTOR, Asm.F(0.5f));
            s.Clear(1, 0xFF000000);
            s.DrawUp(5, 2, LeftStrip((x, y) => new PosPosColor { X = x, Y = y, Z = 0.5f, X1 = x + 1, Y1 = y, Z1 = 0.5f, Color = 0xFF0000FF }));
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFF000000u, px[32 * W + 8]);
            Assert.Equal(0xFF0000FFu, px[32 * W + 24]);
            Assert.Equal(0, s.Skipped);
        }

        /// <summary>A volume texture of two slices, filled through LockBox (one directly, one through a
        /// system-memory texture and UpdateTexture), sampled by depth.</summary>
        [Fact]
        public void VolumeTextures_AreSampledByDepth()
        {
            RequireGpu();
            using var s = new Scene();
            IntPtr Volume(int pool)
            {
                IntPtr v;
                Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateVolumeTexture))(s.Dev, 2, 2, 2, 1, 0, D3DFMT_A8R8G8B8, pool, &v, null), "CreateVolumeTexture");
                return s.Own(v);
            }
            void Fill(IntPtr v, uint front, uint back)
            {
                D3DLOCKED_BOX lb;
                Check(((delegate* unmanaged<IntPtr, uint, D3DLOCKED_BOX*, D3DBOX*, uint, int>)Fn(v, Vtbl.IDirect3DVolumeTexture9.LockBox))(v, 0, &lb, null, 0), "LockBox");
                for (int z = 0; z < 2; z++)
                    for (int y = 0; y < 2; y++)
                        new Span<uint>((byte*)lb.pBits + z * lb.SlicePitch + y * lb.RowPitch, 2).Fill(z == 0 ? front : back);
                Check(((delegate* unmanaged<IntPtr, uint, int>)Fn(v, Vtbl.IDirect3DVolumeTexture9.UnlockBox))(v, 0), "UnlockBox");
            }
            IntPtr direct = Volume(1), staged = Volume(2), updated = Volume(0);
            Fill(direct, 0xFFFF0000, 0xFF00FF00);
            Fill(staged, 0xFF0000FF, 0xFFFFFF00);
            Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.UpdateTexture))(s.Dev, staged, updated), "UpdateTexture");
            D3DVOLUME_DESC desc;
            Check(((delegate* unmanaged<IntPtr, uint, D3DVOLUME_DESC*, int>)Fn(direct, Vtbl.IDirect3DVolumeTexture9.GetLevelDesc))(direct, 0, &desc), "GetLevelDesc");
            Assert.Equal((2u, 2u, 2u), (desc.Width, desc.Height, desc.Depth));
            Assert.Equal(4, ((delegate* unmanaged<IntPtr, int>)Fn(direct, Vtbl.IDirect3DVolumeTexture9.GetType))(direct));

            s.Fvf(FVF_XYZRHW | FVF_TEX1 | 0x10000);          // one set of three coordinates
            s.Clear(1, 0xFF000000);
            RhwDir[] Quad(float x0, float x1, float y0, float y1, float w) => new[]
            {
                new RhwDir { X = x0, Y = y0, Z = 0.5f, Rhw = 1, U = 0.5f, V = 0.5f, W = w }, new RhwDir { X = x1, Y = y0, Z = 0.5f, Rhw = 1, U = 0.5f, V = 0.5f, W = w },
                new RhwDir { X = x0, Y = y1, Z = 0.5f, Rhw = 1, U = 0.5f, V = 0.5f, W = w }, new RhwDir { X = x1, Y = y1, Z = 0.5f, Rhw = 1, U = 0.5f, V = 0.5f, W = w },
            };
            s.Texture(0, direct);
            s.DrawUp(5, 2, Quad(0, 32, 0, 32, 0.25f));
            s.DrawUp(5, 2, Quad(32, 64, 0, 32, 0.75f));
            s.Texture(0, updated);
            s.DrawUp(5, 2, Quad(0, 32, 32, 64, 0.25f));
            s.DrawUp(5, 2, Quad(32, 64, 32, 64, 0.75f));
            s.EndScene();
            uint[] px = s.Pixels();
            Assert.Equal(0xFFFF0000u, px[16 * W + 16]);
            Assert.Equal(0xFF00FF00u, px[16 * W + 48]);
            Assert.Equal(0xFF0000FFu, px[48 * W + 16]);
            Assert.Equal(0xFFFFFF00u, px[48 * W + 48]);
            Assert.Equal(0, s.Skipped);
        }

        /// <summary>An event query is signalled once what came before it is submitted.</summary>
        [Fact]
        public void EventQueries_Signal()
        {
            RequireGpu();
            using var s = new Scene();
            Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateQuery))(s.Dev, 8, null), "CreateQuery (support)");
            IntPtr q;
            Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Fn(s.Dev, Vtbl.IDirect3DDevice9.CreateQuery))(s.Dev, 8, &q), "CreateQuery");
            s.Own(q);
            Check(((delegate* unmanaged<IntPtr, uint, int>)Fn(q, Vtbl.IDirect3DQuery9.Issue))(q, 1), "Issue");
            int done = 0;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, void*, uint, uint, int>)Fn(q, Vtbl.IDirect3DQuery9.GetData))(q, &done, 4, 1));
            Assert.Equal(1, done);
        }
    }
}
