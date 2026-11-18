// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 9 device's state, as d3d9.h's Set*/Get* methods hold it, with D3D9's defaults, and
// state blocks over it. Every value has a key (category << 32 | index) so a state block can record
// the ones set between BeginStateBlock and EndStateBlock, or the ones a D3DSTATEBLOCKTYPE names, and
// capture and apply them.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DVIEWPORT9 { public uint X, Y, Width, Height; public float MinZ, MaxZ; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DCOLORVALUE { public float r, g, b, a; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DMATERIAL9 { public D3DCOLORVALUE Diffuse, Ambient, Specular, Emissive; public float Power; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DLIGHT9
    {
        public int Type;
        public D3DCOLORVALUE Diffuse, Specular, Ambient;
        public float PosX, PosY, PosZ, DirX, DirY, DirZ;
        public float Range, Falloff, Attenuation0, Attenuation1, Attenuation2, Theta, Phi;
    }

    internal static class D3DRS
    {
        public const int ZENABLE = 7, FILLMODE = 8, SHADEMODE = 9, ZWRITEENABLE = 14, ALPHATESTENABLE = 15, LASTPIXEL = 16, SRCBLEND = 19,
            DESTBLEND = 20, CULLMODE = 22, ZFUNC = 23, ALPHAREF = 24, ALPHAFUNC = 25, DITHERENABLE = 26, ALPHABLENDENABLE = 27, FOGENABLE = 28,
            SPECULARENABLE = 29, FOGCOLOR = 34, FOGTABLEMODE = 35, FOGSTART = 36, FOGEND = 37, FOGDENSITY = 38, RANGEFOGENABLE = 48,
            STENCILENABLE = 52, STENCILFAIL = 53, STENCILZFAIL = 54, STENCILPASS = 55, STENCILFUNC = 56, STENCILREF = 57, STENCILMASK = 58,
            STENCILWRITEMASK = 59, TEXTUREFACTOR = 60, CLIPPING = 136, LIGHTING = 137, AMBIENT = 139, FOGVERTEXMODE = 140, COLORVERTEX = 141,
            LOCALVIEWER = 142, NORMALIZENORMALS = 143, DIFFUSEMATERIALSOURCE = 145, SPECULARMATERIALSOURCE = 146, AMBIENTMATERIALSOURCE = 147,
            EMISSIVEMATERIALSOURCE = 148, VERTEXBLEND = 151, CLIPPLANEENABLE = 152, POINTSIZE = 154, POINTSIZE_MIN = 155, POINTSPRITEENABLE = 156,
            POINTSCALEENABLE = 157, POINTSCALE_A = 158, POINTSCALE_B = 159, POINTSCALE_C = 160, MULTISAMPLEANTIALIAS = 161, MULTISAMPLEMASK = 162,
            PATCHEDGESTYLE = 163, DEBUGMONITORTOKEN = 165, POINTSIZE_MAX = 166, INDEXEDVERTEXBLENDENABLE = 167, COLORWRITEENABLE = 168,
            TWEENFACTOR = 170, BLENDOP = 171, POSITIONDEGREE = 172, NORMALDEGREE = 173, SCISSORTESTENABLE = 174, SLOPESCALEDEPTHBIAS = 175,
            ANTIALIASEDLINEENABLE = 176, MINTESSELLATIONLEVEL = 178, MAXTESSELLATIONLEVEL = 179, ADAPTIVETESS_X = 180, ADAPTIVETESS_Y = 181,
            ADAPTIVETESS_Z = 182, ADAPTIVETESS_W = 183, ENABLEADAPTIVETESSELLATION = 184, TWOSIDEDSTENCILMODE = 185, CCW_STENCILFAIL = 186,
            CCW_STENCILZFAIL = 187, CCW_STENCILPASS = 188, CCW_STENCILFUNC = 189, COLORWRITEENABLE1 = 190, COLORWRITEENABLE2 = 191,
            COLORWRITEENABLE3 = 192, BLENDFACTOR = 193, SRGBWRITEENABLE = 194, DEPTHBIAS = 195, SEPARATEALPHABLENDENABLE = 206,
            SRCBLENDALPHA = 207, DESTBLENDALPHA = 208, BLENDOPALPHA = 209;

        public static uint F(float f) => BitConverter.SingleToUInt32Bits(f);

        /// <summary>The render states D3D9 has, with their defaults.</summary>
        public static readonly (int State, uint Value)[] Defaults =
        {
            (ZENABLE, 0), (FILLMODE, 3), (SHADEMODE, 2), (ZWRITEENABLE, 1), (ALPHATESTENABLE, 0), (LASTPIXEL, 1), (SRCBLEND, 2), (DESTBLEND, 1),
            (CULLMODE, 3), (ZFUNC, 4), (ALPHAREF, 0), (ALPHAFUNC, 8), (DITHERENABLE, 0), (ALPHABLENDENABLE, 0), (FOGENABLE, 0), (SPECULARENABLE, 0),
            (FOGCOLOR, 0), (FOGTABLEMODE, 0), (FOGSTART, F(0)), (FOGEND, F(1)), (FOGDENSITY, F(1)), (RANGEFOGENABLE, 0), (STENCILENABLE, 0),
            (STENCILFAIL, 1), (STENCILZFAIL, 1), (STENCILPASS, 1), (STENCILFUNC, 8), (STENCILREF, 0), (STENCILMASK, 0xFFFFFFFF),
            (STENCILWRITEMASK, 0xFFFFFFFF), (TEXTUREFACTOR, 0xFFFFFFFF),
            (128, 0), (129, 0), (130, 0), (131, 0), (132, 0), (133, 0), (134, 0), (135, 0),
            (CLIPPING, 1), (LIGHTING, 1), (AMBIENT, 0), (FOGVERTEXMODE, 0), (COLORVERTEX, 1), (LOCALVIEWER, 1), (NORMALIZENORMALS, 0),
            (DIFFUSEMATERIALSOURCE, 1), (SPECULARMATERIALSOURCE, 2), (AMBIENTMATERIALSOURCE, 0), (EMISSIVEMATERIALSOURCE, 0), (VERTEXBLEND, 0),
            (CLIPPLANEENABLE, 0), (POINTSIZE, F(1)), (POINTSIZE_MIN, F(1)), (POINTSPRITEENABLE, 0), (POINTSCALEENABLE, 0), (POINTSCALE_A, F(1)),
            (POINTSCALE_B, 0), (POINTSCALE_C, 0), (MULTISAMPLEANTIALIAS, 1), (MULTISAMPLEMASK, 0xFFFFFFFF), (PATCHEDGESTYLE, 0),
            (DEBUGMONITORTOKEN, 0xBAADCAFE), (POINTSIZE_MAX, F(64)), (INDEXEDVERTEXBLENDENABLE, 0), (COLORWRITEENABLE, 0xF), (TWEENFACTOR, 0),
            (BLENDOP, 1), (POSITIONDEGREE, 3), (NORMALDEGREE, 1), (SCISSORTESTENABLE, 0), (SLOPESCALEDEPTHBIAS, 0), (ANTIALIASEDLINEENABLE, 0),
            (MINTESSELLATIONLEVEL, F(1)), (MAXTESSELLATIONLEVEL, F(1)), (ADAPTIVETESS_X, 0), (ADAPTIVETESS_Y, 0), (ADAPTIVETESS_Z, F(1)),
            (ADAPTIVETESS_W, 0), (ENABLEADAPTIVETESSELLATION, 0), (TWOSIDEDSTENCILMODE, 0), (CCW_STENCILFAIL, 1), (CCW_STENCILZFAIL, 1),
            (CCW_STENCILPASS, 1), (CCW_STENCILFUNC, 8), (COLORWRITEENABLE1, 0xF), (COLORWRITEENABLE2, 0xF), (COLORWRITEENABLE3, 0xF),
            (BLENDFACTOR, 0xFFFFFFFF), (SRGBWRITEENABLE, 0), (DEPTHBIAS, 0),
            (198, 0), (199, 0), (200, 0), (201, 0), (202, 0), (203, 0), (204, 0), (205, 0),
            (SEPARATEALPHABLENDENABLE, 0), (SRCBLENDALPHA, 2), (DESTBLENDALPHA, 1), (BLENDOPALPHA, 1),
        };

        /// <summary>The render states D3DSBT_PIXELSTATE captures.</summary>
        public static readonly int[] Pixel =
        {
            ZENABLE, FILLMODE, SHADEMODE, ZWRITEENABLE, ALPHATESTENABLE, LASTPIXEL, SRCBLEND, DESTBLEND, ZFUNC, ALPHAREF, ALPHAFUNC, DITHERENABLE,
            FOGSTART, FOGEND, FOGDENSITY, ALPHABLENDENABLE, DEPTHBIAS, STENCILENABLE, STENCILFAIL, STENCILZFAIL, STENCILPASS, STENCILFUNC, STENCILREF,
            STENCILMASK, STENCILWRITEMASK, TEXTUREFACTOR, 128, 129, 130, 131, 132, 133, 134, 135, COLORWRITEENABLE, BLENDOP, SCISSORTESTENABLE,
            SLOPESCALEDEPTHBIAS, ANTIALIASEDLINEENABLE, TWOSIDEDSTENCILMODE, CCW_STENCILFAIL, CCW_STENCILZFAIL, CCW_STENCILPASS, CCW_STENCILFUNC,
            COLORWRITEENABLE1, COLORWRITEENABLE2, COLORWRITEENABLE3, BLENDFACTOR, SRGBWRITEENABLE, 198, 199, 200, 201, 202, 203, 204, 205,
            SEPARATEALPHABLENDENABLE, SRCBLENDALPHA, DESTBLENDALPHA, BLENDOPALPHA,
        };

        /// <summary>The render states D3DSBT_VERTEXSTATE captures.</summary>
        public static readonly int[] Vertex =
        {
            CULLMODE, FOGENABLE, FOGCOLOR, FOGTABLEMODE, FOGSTART, FOGEND, FOGDENSITY, RANGEFOGENABLE, AMBIENT, COLORVERTEX, FOGVERTEXMODE, CLIPPING,
            LIGHTING, LOCALVIEWER, EMISSIVEMATERIALSOURCE, AMBIENTMATERIALSOURCE, DIFFUSEMATERIALSOURCE, SPECULARMATERIALSOURCE, VERTEXBLEND,
            CLIPPLANEENABLE, POINTSIZE, POINTSIZE_MIN, POINTSPRITEENABLE, POINTSCALEENABLE, POINTSCALE_A, POINTSCALE_B, POINTSCALE_C,
            MULTISAMPLEANTIALIAS, MULTISAMPLEMASK, PATCHEDGESTYLE, POINTSIZE_MAX, INDEXEDVERTEXBLENDENABLE, TWEENFACTOR, POSITIONDEGREE,
            NORMALDEGREE, MINTESSELLATIONLEVEL, MAXTESSELLATIONLEVEL, ADAPTIVETESS_X, ADAPTIVETESS_Y, ADAPTIVETESS_Z, ADAPTIVETESS_W,
            ENABLEADAPTIVETESSELLATION, NORMALIZENORMALS, SPECULARENABLE, SHADEMODE,
        };
    }

    /// <summary>The device's state (not the render targets, which no state block holds).</summary>
    internal sealed class D3D9State
    {
        public const int Samplers = 20;     // 0-15 pixel, 16 the displacement map, 17-20 D3DVERTEXTEXTURESAMPLER0-3 (here 16-19)
        public readonly uint[] RS = new uint[256];
        public readonly uint[,] Samp = new uint[Samplers, 14];
        public readonly uint[,] Tss = new uint[8, 33];
        public readonly ComObject?[] Tex = new ComObject?[Samplers];
        public readonly Dictionary<int, float[]> Transforms = new();
        public D3DVIEWPORT9 Viewport = new() { MaxZ = 1 };
        public D3DRECT9 Scissor;
        public D3DMATERIAL9 Material;
        public readonly SortedDictionary<uint, D3DLIGHT9> Lights = new();
        public readonly SortedSet<uint> LightsOn = new();
        public readonly float[] ClipPlanes = new float[6 * 4];
        public readonly float[] VsF = new float[256 * 4], PsF = new float[256 * 4];
        public readonly int[] VsI = new int[16 * 4], PsI = new int[16 * 4];
        public readonly int[] VsB = new int[16], PsB = new int[16];
        public readonly Direct3DBuffer9?[] Streams = new Direct3DBuffer9?[16];
        public readonly uint[] StreamOffset = new uint[16], StreamStride = new uint[16], StreamFreq = new uint[16];
        public Direct3DBuffer9? Indices;
        public Direct3DVertexDeclaration9? Decl;
        public uint Fvf;
        public Direct3DShader9? Vs, Ps;

        /// <summary>Bumped by changes the uniforms of a draw follow: the vertex constants, the pixel
        /// constants, and the device state of the Misc block.</summary>
        public int VsConstVersion, PsConstVersion, MiscVersion;

        /// <summary>The state block being recorded (BeginStateBlock).</summary>
        public Direct3DStateBlock9? Recording;

        public const int CRs = 1, CSamp = 2, CTss = 3, CTex = 4, CTransform = 5, CViewport = 6, CScissor = 7, CMaterial = 8, CLight = 9, CLightOn = 10,
            CClip = 11, CVsF = 12, CVsI = 13, CVsB = 14, CPsF = 15, CPsI = 16, CPsB = 17, CStream = 18, CFreq = 19, CIndices = 20, CDecl = 21, CFvf = 22,
            CVs = 23, CPs = 24;

        public static long Key(int category, int index) => ((long)category << 32) | (uint)index;

        public D3D9State() => Reset();

        public static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public void Reset()
        {
            Array.Clear(RS);
            foreach (var (s, v) in D3DRS.Defaults) RS[s] = v;
            for (int s = 0; s < Samplers; s++)
            {
                for (int t = 0; t < 14; t++) Samp[s, t] = 0;
                Samp[s, 1] = Samp[s, 2] = Samp[s, 3] = 1;      // ADDRESSU/V/W: WRAP
                Samp[s, 5] = Samp[s, 6] = 1;                    // MAG/MINFILTER: POINT
                Samp[s, 10] = 1;                                // MAXANISOTROPY
            }
            for (int i = 0; i < 8; i++)
            {
                for (int t = 0; t < 33; t++) Tss[i, t] = 0;
                Tss[i, 1] = i == 0 ? 4u : 1u;                   // COLOROP: MODULATE, then DISABLE
                Tss[i, 2] = 2; Tss[i, 3] = 1;                   // COLORARG1 TEXTURE, COLORARG2 CURRENT
                Tss[i, 4] = i == 0 ? 2u : 1u;                   // ALPHAOP: SELECTARG1, then DISABLE
                Tss[i, 5] = 2; Tss[i, 6] = 1;
                Tss[i, 11] = (uint)i;                           // TEXCOORDINDEX
                Tss[i, 26] = Tss[i, 27] = Tss[i, 28] = 1;       // ARG0s, RESULTARG: CURRENT
            }
            for (int i = 0; i < Samplers; i++) SetObject(ref Tex[i], null);
            Transforms.Clear();
            Material = default;
            Lights.Clear();
            LightsOn.Clear();
            Array.Clear(ClipPlanes);
            Array.Clear(VsF); Array.Clear(PsF); Array.Clear(VsI); Array.Clear(PsI); Array.Clear(VsB); Array.Clear(PsB);
            for (int i = 0; i < 16; i++) { SetObject(ref Streams[i], null); StreamOffset[i] = StreamStride[i] = 0; StreamFreq[i] = 1; }
            SetObject(ref Indices, null);
            SetObject(ref Decl, null);
            Fvf = 0;
            SetObject(ref Vs, null);
            SetObject(ref Ps, null);
            VsConstVersion++; PsConstVersion++; MiscVersion++;
        }

        public static void SetObject<T>(ref T? slot, T? value) where T : ComObject
        {
            if (ReferenceEquals(slot, value)) return;
            value?.InternalAddRef();
            slot?.InternalRelease();
            slot = value;
        }

        public float[] Transform(int t) => Transforms.TryGetValue(t, out float[]? m) ? m : Identity;

        public void Record(int category, int index)
        {
            if (Recording != null) Recording.Set(Key(category, index), Get(Key(category, index)));
        }

        /// <summary>A state's value, boxed (a state block's copy).</summary>
        public object? Get(long key)
        {
            int c = (int)(key >> 32), i = (int)(uint)key;
            return c switch
            {
                CRs => RS[i],
                CSamp => Samp[i / 16, i % 16],
                CTss => Tss[i / 64, i % 64],
                CTex => Tex[i],
                CTransform => (float[])Transform(i).Clone(),
                CViewport => Viewport,
                CScissor => Scissor,
                CMaterial => Material,
                CLight => Lights.TryGetValue((uint)i, out D3DLIGHT9 l) ? l : null,
                CLightOn => LightsOn.Contains((uint)i),
                CClip => new[] { ClipPlanes[i * 4], ClipPlanes[i * 4 + 1], ClipPlanes[i * 4 + 2], ClipPlanes[i * 4 + 3] },
                CVsF => new[] { VsF[i * 4], VsF[i * 4 + 1], VsF[i * 4 + 2], VsF[i * 4 + 3] },
                CPsF => new[] { PsF[i * 4], PsF[i * 4 + 1], PsF[i * 4 + 2], PsF[i * 4 + 3] },
                CVsI => new[] { VsI[i * 4], VsI[i * 4 + 1], VsI[i * 4 + 2], VsI[i * 4 + 3] },
                CPsI => new[] { PsI[i * 4], PsI[i * 4 + 1], PsI[i * 4 + 2], PsI[i * 4 + 3] },
                CVsB => VsB[i],
                CPsB => PsB[i],
                CStream => new StreamBinding(Streams[i], StreamOffset[i], StreamStride[i]),
                CFreq => StreamFreq[i],
                CIndices => Indices,
                CDecl => Decl,
                CFvf => Fvf,
                CVs => Vs,
                CPs => Ps,
                _ => null,
            };
        }

        /// <summary>A stream's binding, as a state block holds it.</summary>
        public sealed class StreamBinding : ComObjectHolder
        {
            public readonly uint Offset, Stride;
            public StreamBinding(Direct3DBuffer9? b, uint offset, uint stride) : base(b) { Offset = offset; Stride = stride; }
        }

        public void Set(long key, object? v)
        {
            int c = (int)(key >> 32), i = (int)(uint)key;
            switch (c)
            {
                case CRs: RS[i] = (uint)v!; MiscVersion++; break;
                case CSamp: Samp[i / 16, i % 16] = (uint)v!; break;
                case CTss: Tss[i / 64, i % 64] = (uint)v!; MiscVersion++; break;
                case CTex: SetObject(ref Tex[i], (ComObject?)v); break;
                case CTransform: Transforms[i] = (float[])((float[])v!).Clone(); MiscVersion++; break;
                case CViewport: Viewport = (D3DVIEWPORT9)v!; MiscVersion++; break;
                case CScissor: Scissor = (D3DRECT9)v!; break;
                case CMaterial: Material = (D3DMATERIAL9)v!; MiscVersion++; break;
                case CLight: if (v is D3DLIGHT9 l) Lights[(uint)i] = l; else Lights.Remove((uint)i); MiscVersion++; break;
                case CLightOn: if ((bool)v!) LightsOn.Add((uint)i); else LightsOn.Remove((uint)i); MiscVersion++; break;
                case CClip: ((float[])v!).CopyTo(ClipPlanes, i * 4); MiscVersion++; break;
                case CVsF: ((float[])v!).CopyTo(VsF, i * 4); VsConstVersion++; break;
                case CPsF: ((float[])v!).CopyTo(PsF, i * 4); PsConstVersion++; break;
                case CVsI: ((int[])v!).CopyTo(VsI, i * 4); VsConstVersion++; break;
                case CPsI: ((int[])v!).CopyTo(PsI, i * 4); PsConstVersion++; break;
                case CVsB: VsB[i] = (int)v!; VsConstVersion++; break;
                case CPsB: PsB[i] = (int)v!; PsConstVersion++; break;
                case CStream:
                {
                    var s = (StreamBinding)v!;
                    SetObject(ref Streams[i], (Direct3DBuffer9?)s.Object);
                    StreamOffset[i] = s.Offset; StreamStride[i] = s.Stride;
                    break;
                }
                case CFreq: StreamFreq[i] = (uint)v!; break;
                case CIndices: SetObject(ref Indices, (Direct3DBuffer9?)v); break;
                case CDecl: SetObject(ref Decl, (Direct3DVertexDeclaration9?)v); break;
                case CFvf: Fvf = (uint)v!; break;
                case CVs: SetObject(ref Vs, (Direct3DShader9?)v); MiscVersion++; break;
                case CPs: SetObject(ref Ps, (Direct3DShader9?)v); break;
            }
        }

        /// <summary>The keys a D3DSTATEBLOCKTYPE (1 all, 2 pixel, 3 vertex) captures.</summary>
        public IEnumerable<long> KeysOf(int type)
        {
            bool all = type == 1, pixel = all || type == 2, vertex = all || type == 3;
            if (all) foreach (var (s, _) in D3DRS.Defaults) yield return Key(CRs, s);
            else foreach (int s in pixel ? D3DRS.Pixel : D3DRS.Vertex) yield return Key(CRs, s);
            if (pixel)
            {
                for (int s = 0; s < Samplers; s++) for (int t = 1; t < 14; t++) yield return Key(CSamp, s * 16 + t);
                for (int s = 0; s < 8; s++) for (int t = 1; t < 33; t++) yield return Key(CTss, s * 64 + t);
                for (int r = 0; r < 224; r++) yield return Key(CPsF, r);
                for (int r = 0; r < 16; r++) { yield return Key(CPsI, r); yield return Key(CPsB, r); }
                yield return Key(CPs, 0);
            }
            if (vertex)
            {
                for (int r = 0; r < 256; r++) yield return Key(CVsF, r);
                for (int r = 0; r < 16; r++) { yield return Key(CVsI, r); yield return Key(CVsB, r); }
                foreach (uint l in Lights.Keys) { yield return Key(CLight, (int)l); yield return Key(CLightOn, (int)l); }
                for (int i = 0; i < 16; i++) yield return Key(CFreq, i);
                yield return Key(CDecl, 0);
                yield return Key(CFvf, 0);
                yield return Key(CVs, 0);
            }
            if (all)
            {
                for (int s = 0; s < Samplers; s++) yield return Key(CTex, s);
                yield return Key(CTransform, 2);
                yield return Key(CTransform, 3);
                for (int t = 16; t < 24; t++) yield return Key(CTransform, t);
                yield return Key(CTransform, 256);
                foreach (int t in Transforms.Keys) if (t > 256) yield return Key(CTransform, t);
                yield return Key(CViewport, 0);
                yield return Key(CScissor, 0);
                yield return Key(CMaterial, 0);
                for (int i = 0; i < 6; i++) yield return Key(CClip, i);
                for (int i = 0; i < 16; i++) yield return Key(CStream, i);
                yield return Key(CIndices, 0);
            }
        }
    }

    /// <summary>A state block's value that holds a COM object (a stream's buffer with its offset and stride).</summary>
    internal class ComObjectHolder
    {
        public readonly ComObject? Object;
        public ComObjectHolder(ComObject? o) => Object = o;
    }

    internal sealed unsafe partial class Direct3DDevice9
    {
        public readonly D3D9State State = new();

        private static void BuildState(Vtable vt)
        {
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetCreationParameters, (delegate* unmanaged<IntPtr, uint*, int>)&GetCreationParameters);
            vt.Set(Vtbl.IDirect3DDevice9Ex.Reset, (delegate* unmanaged<IntPtr, void*, int>)&Reset);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetBackBuffer, (delegate* unmanaged<IntPtr, uint, uint, int, IntPtr*, int>)&GetBackBuffer);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetNumberOfSwapChains, (delegate* unmanaged<IntPtr, uint>)&GetNumberOfSwapChains);
            vt.Set(Vtbl.IDirect3DDevice9Ex.ResetEx, (delegate* unmanaged<IntPtr, void*, void*, int>)&ResetEx);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetDepthStencilSurface, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetDepthStencilSurface);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetDepthStencilSurface, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDepthStencilSurface);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetTransform, (delegate* unmanaged<IntPtr, int, float*, int>)&SetTransform);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetTransform, (delegate* unmanaged<IntPtr, int, float*, int>)&GetTransform);
            vt.Set(Vtbl.IDirect3DDevice9Ex.MultiplyTransform, (delegate* unmanaged<IntPtr, int, float*, int>)&MultiplyTransform);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetViewport, (delegate* unmanaged<IntPtr, D3DVIEWPORT9*, int>)&SetViewport);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetViewport, (delegate* unmanaged<IntPtr, D3DVIEWPORT9*, int>)&GetViewport);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetMaterial, (delegate* unmanaged<IntPtr, D3DMATERIAL9*, int>)&SetMaterial);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetMaterial, (delegate* unmanaged<IntPtr, D3DMATERIAL9*, int>)&GetMaterial);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetLight, (delegate* unmanaged<IntPtr, uint, D3DLIGHT9*, int>)&SetLight);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetLight, (delegate* unmanaged<IntPtr, uint, D3DLIGHT9*, int>)&GetLight);
            vt.Set(Vtbl.IDirect3DDevice9Ex.LightEnable, (delegate* unmanaged<IntPtr, uint, int, int>)&LightEnable);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetLightEnable, (delegate* unmanaged<IntPtr, uint, int*, int>)&GetLightEnable);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetClipPlane, (delegate* unmanaged<IntPtr, uint, float*, int>)&SetClipPlane);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetClipPlane, (delegate* unmanaged<IntPtr, uint, float*, int>)&GetClipPlane);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetRenderState, (delegate* unmanaged<IntPtr, int, uint, int>)&SetRenderState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetRenderState, (delegate* unmanaged<IntPtr, int, uint*, int>)&GetRenderState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateStateBlock, (delegate* unmanaged<IntPtr, int, IntPtr*, int>)&CreateStateBlock);
            vt.Set(Vtbl.IDirect3DDevice9Ex.BeginStateBlock, (delegate* unmanaged<IntPtr, int>)&BeginStateBlock);
            vt.Set(Vtbl.IDirect3DDevice9Ex.EndStateBlock, (delegate* unmanaged<IntPtr, IntPtr*, int>)&EndStateBlock);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetClipStatus, (delegate* unmanaged<IntPtr, void*, int>)&ClipStatus);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetClipStatus, (delegate* unmanaged<IntPtr, void*, int>)&GetClipStatus);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetTexture, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetTexture);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetTexture, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&SetTexture);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetTextureStageState, (delegate* unmanaged<IntPtr, uint, int, uint*, int>)&GetTextureStageState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetTextureStageState, (delegate* unmanaged<IntPtr, uint, int, uint, int>)&SetTextureStageState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetSamplerState, (delegate* unmanaged<IntPtr, uint, int, uint*, int>)&GetSamplerState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetSamplerState, (delegate* unmanaged<IntPtr, uint, int, uint, int>)&SetSamplerState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.ValidateDevice, (delegate* unmanaged<IntPtr, uint*, int>)&ValidateDevice);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetPaletteEntries, (delegate* unmanaged<IntPtr, uint, void*, int>)&PaletteEntries);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetPaletteEntries, (delegate* unmanaged<IntPtr, uint, void*, int>)&PaletteEntries);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetCurrentTexturePalette, (delegate* unmanaged<IntPtr, uint, int>)&Ok1u);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetCurrentTexturePalette, (delegate* unmanaged<IntPtr, uint*, int>)&GetCurrentTexturePalette);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetScissorRect, (delegate* unmanaged<IntPtr, D3DRECT9*, int>)&SetScissorRect);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetScissorRect, (delegate* unmanaged<IntPtr, D3DRECT9*, int>)&GetScissorRect);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetSoftwareVertexProcessing, (delegate* unmanaged<IntPtr, int, int>)&Ok1);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetSoftwareVertexProcessing, (delegate* unmanaged<IntPtr, int>)&GetSoftwareVertexProcessing);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetNPatchMode, (delegate* unmanaged<IntPtr, float, int>)&SetNPatchMode);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetNPatchMode, (delegate* unmanaged<IntPtr, float>)&GetNPatchMode);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateVertexDeclaration, (delegate* unmanaged<IntPtr, D3DVERTEXELEMENT9*, IntPtr*, int>)&CreateVertexDeclaration);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetVertexDeclaration, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetVertexDeclaration);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetVertexDeclaration, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetVertexDeclaration);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetFVF, (delegate* unmanaged<IntPtr, uint, int>)&SetFVF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetFVF, (delegate* unmanaged<IntPtr, uint*, int>)&GetFVF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateVertexShader, (delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)&CreateVertexShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetVertexShader, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetVertexShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetVertexShader, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetVertexShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetVertexShaderConstantF, (delegate* unmanaged<IntPtr, uint, float*, uint, int>)&SetVertexShaderConstantF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetVertexShaderConstantF, (delegate* unmanaged<IntPtr, uint, float*, uint, int>)&GetVertexShaderConstantF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetVertexShaderConstantI, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&SetVertexShaderConstantI);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetVertexShaderConstantI, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&GetVertexShaderConstantI);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetVertexShaderConstantB, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&SetVertexShaderConstantB);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetVertexShaderConstantB, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&GetVertexShaderConstantB);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetStreamSource, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, uint, int>)&SetStreamSource);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetStreamSource, (delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, uint*, int>)&GetStreamSource);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetStreamSourceFreq, (delegate* unmanaged<IntPtr, uint, uint, int>)&SetStreamSourceFreq);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetStreamSourceFreq, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetStreamSourceFreq);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetIndices, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetIndices);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetIndices, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetIndices);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreatePixelShader, (delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)&CreatePixelShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetPixelShader, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetPixelShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetPixelShader, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetPixelShader);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetPixelShaderConstantF, (delegate* unmanaged<IntPtr, uint, float*, uint, int>)&SetPixelShaderConstantF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetPixelShaderConstantF, (delegate* unmanaged<IntPtr, uint, float*, uint, int>)&GetPixelShaderConstantF);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetPixelShaderConstantI, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&SetPixelShaderConstantI);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetPixelShaderConstantI, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&GetPixelShaderConstantI);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetPixelShaderConstantB, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&SetPixelShaderConstantB);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetPixelShaderConstantB, (delegate* unmanaged<IntPtr, uint, int*, uint, int>)&GetPixelShaderConstantB);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateVertexBuffer, (delegate* unmanaged<IntPtr, uint, uint, uint, int, IntPtr*, IntPtr*, int>)&CreateVertexBuffer);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateIndexBuffer, (delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateIndexBuffer);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateQuery, (delegate* unmanaged<IntPtr, int, IntPtr*, int>)&CreateQuery);
        }

        private static Direct3DDevice9? Dev(IntPtr p) => From<Direct3DDevice9>(p);
        private const int Invalid = D3D9Const.D3DERR_INVALIDCALL;

        // ---- creation parameters, reset -------------------------------------------------------------

        public uint BehaviorFlags;
        public IntPtr FocusWindow;

        [UnmanagedCallersOnly]
        private static int GetCreationParameters(IntPtr self, uint* p)
        {
            var d = Dev(self);
            if (d == null || p == null) return Invalid;
            p[0] = 0; p[1] = 1;          // AdapterOrdinal, D3DDEVTYPE_HAL
            *(IntPtr*)(p + 2) = d.FocusWindow;
            p[2 + IntPtr.Size / 4] = d.BehaviorFlags;
            return HResult.S_OK;
        }

        /// <summary>Reset: the device's state goes back to its defaults (D3D9Ex keeps its resources).</summary>
        [UnmanagedCallersOnly] private static int Reset(IntPtr self, void* pp) => ResetCore(self, (D3DPRESENT_PARAMETERS*)pp);
        [UnmanagedCallersOnly] private static int ResetEx(IntPtr self, void* pp, void* mode) => ResetCore(self, (D3DPRESENT_PARAMETERS*)pp);

        private static int ResetCore(IntPtr self, D3DPRESENT_PARAMETERS* pp)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            d.FlushDraws();
            d.State.Reset();
            return pp != null ? d.MakeSwapChain(*pp) : HResult.S_OK;
        }

        // ---- the implicit swap chain: its back buffers and the automatic depth-stencil surface ----------

        private Direct3DSurface9?[] _backBuffers = Array.Empty<Direct3DSurface9?>();
        public D3DPRESENT_PARAMETERS Presentation;

        /// <summary>The device's back buffers (render target 0 to begin with) and, with
        /// EnableAutoDepthStencil, its depth-stencil surface (and D3DRS_ZENABLE on), from the present
        /// parameters. A back buffer of size 0 is the focus window's client area -- a size the shim has
        /// no window for, so 1x1, as a window-less D3DImage device asks.</summary>
        public int MakeSwapChain(D3DPRESENT_PARAMETERS pp)
        {
            foreach (var b in _backBuffers) b?.InternalRelease();
            _backBuffers = Array.Empty<Direct3DSurface9?>();
            uint w = Math.Max(1, pp.BackBufferWidth), h = Math.Max(1, pp.BackBufferHeight);
            int format = pp.BackBufferFormat == 0 ? D3D9Const.D3DFMT_X8R8G8B8 : pp.BackBufferFormat;
            if (D3D9Const.ToDxgi(format) == 0) return D3D9Const.D3DERR_NOTAVAILABLE;
            int count = (int)Math.Clamp(pp.BackBufferCount, 1u, 3u);
            pp.BackBufferWidth = w; pp.BackBufferHeight = h; pp.BackBufferFormat = format; pp.BackBufferCount = (uint)count;
            Presentation = pp;
            _backBuffers = new Direct3DSurface9?[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr p;
                int hr = MakeSurface(w, h, format, D3D9Const.D3DPOOL_DEFAULT, D3D9Const.D3DUSAGE_RENDERTARGET, true, &p, null);
                if (hr < 0) return hr;
                var s = From<Direct3DSurface9>(p)!;
                s.InternalAddRef();
                s.Release();
                _backBuffers[i] = s;
            }
            lock (DrawLock)
            {
                for (int i = 1; i < 4; i++) D3D9State.SetObject(ref Targets[i], null);
                SetTarget(0, _backBuffers[0]);
                D3D9State.SetObject(ref DepthStencil, null);
                if (pp.EnableAutoDepthStencil != 0)
                {
                    IntPtr p;
                    int hr = MakeSurface(w, h, pp.AutoDepthStencilFormat, D3D9Const.D3DPOOL_DEFAULT, D3D9Const.D3DUSAGE_DEPTHSTENCIL, false, &p, null);
                    if (hr < 0) return hr;
                    var ds = From<Direct3DSurface9>(p)!;
                    D3D9State.SetObject(ref DepthStencil, ds);
                    ds.Release();
                    State.RS[D3DRS.ZENABLE] = 1;
                }
            }
            return HResult.S_OK;
        }

        private void ReleaseSwapChain()
        {
            foreach (var b in _backBuffers) b?.InternalRelease();
            _backBuffers = Array.Empty<Direct3DSurface9?>();
        }

        [UnmanagedCallersOnly]
        private static int GetBackBuffer(IntPtr self, uint swapChain, uint index, int type, IntPtr* surface)
        {
            var d = Dev(self);
            if (d == null || surface == null || swapChain != 0 || index >= d._backBuffers.Length) return Invalid;
            *surface = d._backBuffers[index]!.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static uint GetNumberOfSwapChains(IntPtr self) => 1;

        // ---- render targets ----------------------------------------------------------------------------

        public readonly Direct3DSurface9?[] Targets = new Direct3DSurface9?[4];
        public Direct3DSurface9? DepthStencil;

        [UnmanagedCallersOnly]
        private static int SetDepthStencilSurface(IntPtr self, IntPtr surface)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            var s = From<Direct3DSurface9>(surface);
            D3D9State.SetObject(ref d.DepthStencil, s);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetDepthStencilSurface(IntPtr self, IntPtr* surface)
        {
            var d = Dev(self);
            if (d == null || surface == null) return Invalid;
            *surface = d.DepthStencil?.AddRefPointer() ?? IntPtr.Zero;
            return d.DepthStencil == null ? D3D9Const.D3DERR_NOTFOUND : HResult.S_OK;
        }

        // ---- transforms, viewport, material, lights, clip planes --------------------------------------

        [UnmanagedCallersOnly]
        private static int SetTransform(IntPtr self, int state, float* m)
        {
            var d = Dev(self);
            if (d == null || m == null) return Invalid;
            d.State.Transforms[state] = new ReadOnlySpan<float>(m, 16).ToArray();
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CTransform, state);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetTransform(IntPtr self, int state, float* m)
        {
            var d = Dev(self);
            if (d == null || m == null) return Invalid;
            d.State.Transform(state).AsSpan().CopyTo(new Span<float>(m, 16));
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int MultiplyTransform(IntPtr self, int state, float* m)
        {
            var d = Dev(self);
            if (d == null || m == null) return Invalid;
            d.State.Transforms[state] = D3D9Math.Mul(new ReadOnlySpan<float>(m, 16).ToArray(), d.State.Transform(state));
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CTransform, state);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetViewport(IntPtr self, D3DVIEWPORT9* v)
        {
            var d = Dev(self);
            if (d == null || v == null || v->Width == 0 || v->Height == 0) return Invalid;
            d.State.Viewport = *v;
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CViewport, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetViewport(IntPtr self, D3DVIEWPORT9* v)
        {
            var d = Dev(self);
            if (d == null || v == null) return Invalid;
            *v = d.State.Viewport;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetMaterial(IntPtr self, D3DMATERIAL9* m)
        {
            var d = Dev(self);
            if (d == null || m == null) return Invalid;
            d.State.Material = *m;
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CMaterial, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetMaterial(IntPtr self, D3DMATERIAL9* m)
        {
            var d = Dev(self);
            if (d == null || m == null) return Invalid;
            *m = d.State.Material;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetLight(IntPtr self, uint index, D3DLIGHT9* l)
        {
            var d = Dev(self);
            if (d == null || l == null || l->Type is < 1 or > 3) return Invalid;
            d.State.Lights[index] = *l;
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CLight, (int)index);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetLight(IntPtr self, uint index, D3DLIGHT9* l)
        {
            var d = Dev(self);
            if (d == null || l == null || !d.State.Lights.TryGetValue(index, out D3DLIGHT9 v)) return Invalid;
            *l = v;
            return HResult.S_OK;
        }

        /// <summary>A light enabled before SetLight is D3D9's default: white, directional, down +z.</summary>
        public static readonly D3DLIGHT9 DefaultLight = new()
        {
            Type = 3, Diffuse = new D3DCOLORVALUE { r = 1, g = 1, b = 1 }, DirZ = 1,
        };

        [UnmanagedCallersOnly]
        private static int LightEnable(IntPtr self, uint index, int enable)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            if (!d.State.Lights.ContainsKey(index)) { d.State.Lights[index] = DefaultLight; d.State.Record(D3D9State.CLight, (int)index); }
            if (enable != 0) d.State.LightsOn.Add(index); else d.State.LightsOn.Remove(index);
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CLightOn, (int)index);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetLightEnable(IntPtr self, uint index, int* enable)
        {
            var d = Dev(self);
            if (d == null || enable == null || !d.State.Lights.ContainsKey(index)) return Invalid;
            *enable = d.State.LightsOn.Contains(index) ? 128 : 0;      // D3D9 answers 128 for TRUE
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetClipPlane(IntPtr self, uint index, float* plane)
        {
            var d = Dev(self);
            if (d == null || plane == null || index >= 6) return Invalid;
            new ReadOnlySpan<float>(plane, 4).CopyTo(d.State.ClipPlanes.AsSpan((int)index * 4));
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CClip, (int)index);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetClipPlane(IntPtr self, uint index, float* plane)
        {
            var d = Dev(self);
            if (d == null || plane == null || index >= 6) return Invalid;
            d.State.ClipPlanes.AsSpan((int)index * 4, 4).CopyTo(new Span<float>(plane, 4));
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int ClipStatus(IntPtr self, void* s) => HResult.S_OK;

        [UnmanagedCallersOnly]
        private static int GetClipStatus(IntPtr self, void* s)
        {
            if (s != null) new Span<byte>(s, 8).Clear();
            return HResult.S_OK;
        }

        // ---- render, sampler and texture stage states ----------------------------------------------

        [UnmanagedCallersOnly]
        private static int SetRenderState(IntPtr self, int state, uint value)
        {
            var d = Dev(self);
            if (d == null || state is < 0 or > 255) return Invalid;
            d.State.RS[state] = value;
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CRs, state);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetRenderState(IntPtr self, int state, uint* value)
        {
            var d = Dev(self);
            if (d == null || value == null || state is < 0 or > 255) return Invalid;
            *value = d.State.RS[state];
            return HResult.S_OK;
        }

        /// <summary>A sampler number as the state's index: 0-15, D3DDMAPSAMPLER (256) and D3DVERTEXTEXTURESAMPLER0-3 (257-260) as 16-19.</summary>
        private static int SamplerIndex(uint s) => s < 16 ? (int)s : s is >= 257 and <= 260 ? (int)(s - 257 + 16) : -1;

        [UnmanagedCallersOnly]
        private static int SetSamplerState(IntPtr self, uint sampler, int type, uint value)
        {
            var d = Dev(self);
            int s = SamplerIndex(sampler);
            if (d == null || s < 0 || type is < 1 or > 13) return s < 0 && sampler == 256 ? HResult.S_OK : Invalid;
            d.State.Samp[s, type] = value;
            d.State.Record(D3D9State.CSamp, s * 16 + type);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetSamplerState(IntPtr self, uint sampler, int type, uint* value)
        {
            var d = Dev(self);
            int s = SamplerIndex(sampler);
            if (d == null || value == null || s < 0 || type is < 1 or > 13) return Invalid;
            *value = d.State.Samp[s, type];
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetTextureStageState(IntPtr self, uint stage, int type, uint value)
        {
            var d = Dev(self);
            if (d == null || stage >= 8 || type is < 1 or > 32) return stage >= 8 && stage < 16 ? HResult.S_OK : Invalid;
            d.State.Tss[stage, type] = value;
            d.State.MiscVersion++;
            d.State.Record(D3D9State.CTss, (int)stage * 64 + type);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetTextureStageState(IntPtr self, uint stage, int type, uint* value)
        {
            var d = Dev(self);
            if (d == null || value == null || stage >= 8 || type is < 1 or > 32) return Invalid;
            *value = d.State.Tss[stage, type];
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetTexture(IntPtr self, uint sampler, IntPtr texture)
        {
            var d = Dev(self);
            int s = SamplerIndex(sampler);
            if (d == null || s < 0) return sampler == 256 ? HResult.S_OK : Invalid;
            var t = From(texture);
            if (texture != IntPtr.Zero && t is not Direct3DTexture9) return Invalid;
            D3D9State.SetObject(ref d.State.Tex[s], t);
            d.State.Record(D3D9State.CTex, s);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetTexture(IntPtr self, uint sampler, IntPtr* texture)
        {
            var d = Dev(self);
            int s = SamplerIndex(sampler);
            if (d == null || texture == null || s < 0) return Invalid;
            *texture = d.State.Tex[s]?.AddRefPointer() ?? IntPtr.Zero;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ValidateDevice(IntPtr self, uint* passes)
        {
            if (passes != null) *passes = 1;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int PaletteEntries(IntPtr self, uint n, void* entries) => HResult.S_OK;

        [UnmanagedCallersOnly]
        private static int GetCurrentTexturePalette(IntPtr self, uint* n)
        {
            if (n != null) *n = 0;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetScissorRect(IntPtr self, D3DRECT9* r)
        {
            var d = Dev(self);
            if (d == null || r == null) return Invalid;
            d.State.Scissor = *r;
            d.State.Record(D3D9State.CScissor, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetScissorRect(IntPtr self, D3DRECT9* r)
        {
            var d = Dev(self);
            if (d == null || r == null) return Invalid;
            *r = d.State.Scissor;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetSoftwareVertexProcessing(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static int SetNPatchMode(IntPtr self, float segments) => HResult.S_OK;
        [UnmanagedCallersOnly] private static float GetNPatchMode(IntPtr self) => 0f;

        // ---- state blocks --------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int CreateStateBlock(IntPtr self, int type, IntPtr* block)
        {
            var d = Dev(self);
            if (d == null || block == null || type is < 1 or > 3) return Invalid;
            var b = new Direct3DStateBlock9(d);
            foreach (long k in d.State.KeysOf(type)) b.Set(k, d.State.Get(k));
            *block = b.Pointer;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int BeginStateBlock(IntPtr self)
        {
            var d = Dev(self);
            if (d == null || d.State.Recording != null) return Invalid;
            d.State.Recording = new Direct3DStateBlock9(d);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int EndStateBlock(IntPtr self, IntPtr* block)
        {
            var d = Dev(self);
            if (d == null || block == null || d.State.Recording == null) return Invalid;
            *block = d.State.Recording.Pointer;
            d.State.Recording = null;
            return HResult.S_OK;
        }

        // ---- vertex declarations, streams, indices -------------------------------------------------

        [UnmanagedCallersOnly]
        private static int CreateVertexDeclaration(IntPtr self, D3DVERTEXELEMENT9* elements, IntPtr* decl)
        {
            var d = Dev(self);
            if (d == null || elements == null || decl == null) return Invalid;
            var list = new List<D3DVERTEXELEMENT9>();
            for (int i = 0; elements[i].Stream != 0xFF && i < 64; i++)
            {
                if (Direct3DVertexDeclaration9.TypeBytes(elements[i].Type) == 0) return Invalid;
                list.Add(elements[i]);
            }
            *decl = new Direct3DVertexDeclaration9(d, list.ToArray()).Pointer;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetVertexDeclaration(IntPtr self, IntPtr decl)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            D3D9State.SetObject(ref d.State.Decl, From<Direct3DVertexDeclaration9>(decl));
            d.State.Record(D3D9State.CDecl, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetVertexDeclaration(IntPtr self, IntPtr* decl)
        {
            var d = Dev(self);
            if (d == null || decl == null) return Invalid;
            *decl = d.State.Decl?.AddRefPointer() ?? IntPtr.Zero;
            return HResult.S_OK;
        }

        private readonly Dictionary<uint, Direct3DVertexDeclaration9> _fvfDecls = new();

        /// <summary>The declaration of a D3DFVF code (SetFVF sets it as the vertex declaration, as D3D9 does).</summary>
        public Direct3DVertexDeclaration9 DeclOf(uint fvf)
        {
            if (!_fvfDecls.TryGetValue(fvf, out var decl))
            {
                decl = new Direct3DVertexDeclaration9(this, Direct3DVertexDeclaration9.FromFvf(fvf), fvf);
                decl.InternalAddRef();
                decl.Release();
                _fvfDecls[fvf] = decl;
            }
            return decl;
        }

        [UnmanagedCallersOnly]
        private static int SetFVF(IntPtr self, uint fvf)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            d.State.Fvf = fvf;
            D3D9State.SetObject(ref d.State.Decl, fvf == 0 ? null : d.DeclOf(fvf));
            d.State.Record(D3D9State.CFvf, 0);
            d.State.Record(D3D9State.CDecl, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetFVF(IntPtr self, uint* fvf)
        {
            var d = Dev(self);
            if (d == null || fvf == null) return Invalid;
            *fvf = d.State.Decl?.Fvf ?? 0;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetStreamSource(IntPtr self, uint stream, IntPtr vb, uint offset, uint stride)
        {
            var d = Dev(self);
            if (d == null || stream >= 16) return Invalid;
            var b = From<Direct3DBuffer9>(vb);
            if (vb != IntPtr.Zero && (b == null || b.IsIndex)) return Invalid;
            D3D9State.SetObject(ref d.State.Streams[stream], b);
            d.State.StreamOffset[stream] = offset;
            d.State.StreamStride[stream] = stride;
            d.State.Record(D3D9State.CStream, (int)stream);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSource(IntPtr self, uint stream, IntPtr* vb, uint* offset, uint* stride)
        {
            var d = Dev(self);
            if (d == null || stream >= 16 || vb == null) return Invalid;
            *vb = d.State.Streams[stream]?.AddRefPointer() ?? IntPtr.Zero;
            if (offset != null) *offset = d.State.StreamOffset[stream];
            if (stride != null) *stride = d.State.StreamStride[stream];
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetStreamSourceFreq(IntPtr self, uint stream, uint freq)
        {
            var d = Dev(self);
            if (d == null || stream >= 16) return Invalid;
            d.State.StreamFreq[stream] = freq;
            d.State.Record(D3D9State.CFreq, (int)stream);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSourceFreq(IntPtr self, uint stream, uint* freq)
        {
            var d = Dev(self);
            if (d == null || stream >= 16 || freq == null) return Invalid;
            *freq = d.State.StreamFreq[stream];
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetIndices(IntPtr self, IntPtr ib)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            var b = From<Direct3DBuffer9>(ib);
            if (ib != IntPtr.Zero && (b == null || !b.IsIndex)) return Invalid;
            D3D9State.SetObject(ref d.State.Indices, b);
            d.State.Record(D3D9State.CIndices, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetIndices(IntPtr self, IntPtr* ib)
        {
            var d = Dev(self);
            if (d == null || ib == null) return Invalid;
            *ib = d.State.Indices?.AddRefPointer() ?? IntPtr.Zero;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreateVertexBuffer(IntPtr self, uint length, uint usage, uint fvf, int pool, IntPtr* vb, IntPtr* shared)
        {
            var d = Dev(self);
            if (d == null || vb == null || length == 0 || (shared != null && *shared != IntPtr.Zero)) return Invalid;
            *vb = new Direct3DBuffer9(d, false, length, usage, fvf, Direct3DBuffer9.D3DFMT_VERTEXDATA, pool).Pointer;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreateIndexBuffer(IntPtr self, uint length, uint usage, int format, int pool, IntPtr* ib, IntPtr* shared)
        {
            var d = Dev(self);
            if (d == null || ib == null || length == 0 || format is not (Direct3DBuffer9.D3DFMT_INDEX16 or Direct3DBuffer9.D3DFMT_INDEX32) || (shared != null && *shared != IntPtr.Zero)) return Invalid;
            *ib = new Direct3DBuffer9(d, true, length, usage, 0, format, pool).Pointer;
            return HResult.S_OK;
        }

        // ---- shaders and their constants ---------------------------------------------------------------

        private static int CreateShader(IntPtr self, uint* function, IntPtr* shader, bool pixel)
        {
            var d = Dev(self);
            if (d == null || function == null || shader == null) return Invalid;
            *shader = IntPtr.Zero;
            try
            {
                int n = Direct3DShader9.Length(function);
                var code = D3D9Bytecode.Translate(new ReadOnlySpan<byte>(function, n), pixel);
                *shader = new Direct3DShader9(d, code).Pointer;
                return HResult.S_OK;
            }
            catch (Exception e) { D3DLog.Error(pixel ? "CreatePixelShader" : "CreateVertexShader", e); return Invalid; }
        }

        [UnmanagedCallersOnly] private static int CreateVertexShader(IntPtr self, uint* function, IntPtr* shader) => CreateShader(self, function, shader, false);
        [UnmanagedCallersOnly] private static int CreatePixelShader(IntPtr self, uint* function, IntPtr* shader) => CreateShader(self, function, shader, true);

        [UnmanagedCallersOnly]
        private static int SetVertexShader(IntPtr self, IntPtr shader)
        {
            var d = Dev(self);
            var s = From<Direct3DShader9>(shader);
            if (d == null || (shader != IntPtr.Zero && (s == null || s.Code.Pixel))) return Invalid;
            D3D9State.SetObject(ref d.State.Vs, s);
            d.State.MiscVersion++;      // the clip planes' space follows it
            d.State.Record(D3D9State.CVs, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetPixelShader(IntPtr self, IntPtr shader)
        {
            var d = Dev(self);
            var s = From<Direct3DShader9>(shader);
            if (d == null || (shader != IntPtr.Zero && (s == null || !s.Code.Pixel))) return Invalid;
            D3D9State.SetObject(ref d.State.Ps, s);
            d.State.Record(D3D9State.CPs, 0);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetVertexShader(IntPtr self, IntPtr* shader)
        {
            var d = Dev(self);
            if (d == null || shader == null) return Invalid;
            *shader = d.State.Vs?.AddRefPointer() ?? IntPtr.Zero;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetPixelShader(IntPtr self, IntPtr* shader)
        {
            var d = Dev(self);
            if (d == null || shader == null) return Invalid;
            *shader = d.State.Ps?.AddRefPointer() ?? IntPtr.Zero;
            return HResult.S_OK;
        }

        private static int SetConstF(IntPtr self, uint start, float* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 256) return Invalid;
            new ReadOnlySpan<float>(data, (int)count * 4).CopyTo((pixel ? d.State.PsF : d.State.VsF).AsSpan((int)start * 4));
            if (pixel) d.State.PsConstVersion++; else d.State.VsConstVersion++;
            for (uint r = start; r < start + count; r++) d.State.Record(pixel ? D3D9State.CPsF : D3D9State.CVsF, (int)r);
            return HResult.S_OK;
        }

        private static int GetConstF(IntPtr self, uint start, float* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 256) return Invalid;
            (pixel ? d.State.PsF : d.State.VsF).AsSpan((int)start * 4, (int)count * 4).CopyTo(new Span<float>(data, (int)count * 4));
            return HResult.S_OK;
        }

        private static int SetConstI(IntPtr self, uint start, int* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 16) return Invalid;
            new ReadOnlySpan<int>(data, (int)count * 4).CopyTo((pixel ? d.State.PsI : d.State.VsI).AsSpan((int)start * 4));
            if (pixel) d.State.PsConstVersion++; else d.State.VsConstVersion++;
            for (uint r = start; r < start + count; r++) d.State.Record(pixel ? D3D9State.CPsI : D3D9State.CVsI, (int)r);
            return HResult.S_OK;
        }

        private static int GetConstI(IntPtr self, uint start, int* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 16) return Invalid;
            (pixel ? d.State.PsI : d.State.VsI).AsSpan((int)start * 4, (int)count * 4).CopyTo(new Span<int>(data, (int)count * 4));
            return HResult.S_OK;
        }

        private static int SetConstB(IntPtr self, uint start, int* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 16) return Invalid;
            for (uint i = 0; i < count; i++) (pixel ? d.State.PsB : d.State.VsB)[start + i] = data[i] != 0 ? 1 : 0;
            if (pixel) d.State.PsConstVersion++; else d.State.VsConstVersion++;
            for (uint r = start; r < start + count; r++) d.State.Record(pixel ? D3D9State.CPsB : D3D9State.CVsB, (int)r);
            return HResult.S_OK;
        }

        private static int GetConstB(IntPtr self, uint start, int* data, uint count, bool pixel)
        {
            var d = Dev(self);
            if (d == null || data == null || start + count > 16) return Invalid;
            for (uint i = 0; i < count; i++) data[i] = (pixel ? d.State.PsB : d.State.VsB)[start + i];
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetVertexShaderConstantF(IntPtr self, uint s, float* d, uint n) => SetConstF(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int GetVertexShaderConstantF(IntPtr self, uint s, float* d, uint n) => GetConstF(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int SetPixelShaderConstantF(IntPtr self, uint s, float* d, uint n) => SetConstF(self, s, d, n, true);
        [UnmanagedCallersOnly] private static int GetPixelShaderConstantF(IntPtr self, uint s, float* d, uint n) => GetConstF(self, s, d, n, true);
        [UnmanagedCallersOnly] private static int SetVertexShaderConstantI(IntPtr self, uint s, int* d, uint n) => SetConstI(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int GetVertexShaderConstantI(IntPtr self, uint s, int* d, uint n) => GetConstI(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int SetPixelShaderConstantI(IntPtr self, uint s, int* d, uint n) => SetConstI(self, s, d, n, true);
        [UnmanagedCallersOnly] private static int GetPixelShaderConstantI(IntPtr self, uint s, int* d, uint n) => GetConstI(self, s, d, n, true);
        [UnmanagedCallersOnly] private static int SetVertexShaderConstantB(IntPtr self, uint s, int* d, uint n) => SetConstB(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int GetVertexShaderConstantB(IntPtr self, uint s, int* d, uint n) => GetConstB(self, s, d, n, false);
        [UnmanagedCallersOnly] private static int SetPixelShaderConstantB(IntPtr self, uint s, int* d, uint n) => SetConstB(self, s, d, n, true);
        [UnmanagedCallersOnly] private static int GetPixelShaderConstantB(IntPtr self, uint s, int* d, uint n) => GetConstB(self, s, d, n, true);

        // ---- queries ----------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int CreateQuery(IntPtr self, int type, IntPtr* query)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            if (!Direct3DQuery9.Supported(type)) return D3D9Const.D3DERR_NOTAVAILABLE;
            if (query != null) *query = new Direct3DQuery9(d, type).Pointer;      // a null pointer asks whether the type is supported
            return HResult.S_OK;
        }
    }

    /// <summary>4x4 matrices as D3D9 lays them out (row-major, row vectors: v' = v * M).</summary>
    internal static class D3D9Math
    {
        public static float[] Mul(float[] a, float[] b)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
            return r;
        }

        public static float[] Transpose(float[] m)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) r[i * 4 + j] = m[j * 4 + i];
            return r;
        }

        public static float[] Inverse(float[] m)
        {
            var inv = new float[16];
            inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
            inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
            inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
            inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
            inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
            inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
            inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
            inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
            inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
            inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
            inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
            inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
            inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
            inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
            inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
            inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
            float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
            if (det == 0) return (float[])D3D9State.Identity.Clone();
            for (int i = 0; i < 16; i++) inv[i] /= det;
            return inv;
        }
    }
}
