// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 9 device's drawing objects: vertex and index buffers, vertex declarations, vertex and
// pixel shaders, queries and state blocks (d3d9.h). Buffers keep their bytes in memory, as a managed
// or dynamic D3D9 buffer does, and are copied to the GPU before a draw that reads them.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DVERTEXELEMENT9 { public ushort Stream, Offset; public byte Type, Method, Usage, UsageIndex; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DVERTEXBUFFER_DESC { public int Format, Type; public uint Usage; public int Pool; public uint Size, FVF; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DINDEXBUFFER_DESC { public int Format, Type; public uint Usage; public int Pool; public uint Size; }

    internal abstract unsafe class D3D9Child : ComObject
    {
        public readonly Direct3DDevice9 Device;

        protected D3D9Child(Direct3DDevice9 device, Vtable vt) : base(vt)
        {
            Device = device;
            device.InternalAddRef();
        }

        protected override void OnFinalRelease()
        {
            Released();
            Device.InternalRelease();
        }

        protected virtual void Released() { }

        protected static void FillChild(Vtable vt) => vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDevice);

        [UnmanagedCallersOnly]
        private static int GetDevice(IntPtr self, IntPtr* device)
        {
            var c = From<D3D9Child>(self);
            if (c == null || device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = c.Device.AddRefPointer();
            return HResult.S_OK;
        }
    }

    // ---- IDirect3DVertexBuffer9 / IDirect3DIndexBuffer9 -----------------------------------------

    internal sealed unsafe class Direct3DBuffer9 : D3D9Child
    {
        private static readonly Vtable s_vb = Build("IDirect3DVertexBuffer9", Vtbl.IDirect3DVertexBuffer9.IID);
        private static readonly Vtable s_ib = Build("IDirect3DIndexBuffer9", Vtbl.IDirect3DIndexBuffer9.IID);
        public const int D3DRTYPE_VERTEXBUFFER = 6, D3DRTYPE_INDEXBUFFER = 7, D3DFMT_VERTEXDATA = 100, D3DFMT_INDEX16 = 101, D3DFMT_INDEX32 = 102;

        public readonly bool IsIndex;
        public readonly uint Size, Usage, Fvf;
        public readonly int Format, Pool;
        /// <summary>The buffer's bytes (rounded up to 4).</summary>
        public readonly byte* Cpu;
        private readonly uint _alloc;
        /// <summary>The GPU copy, current unless <see cref="Dirty"/>.</summary>
        public IntPtr Gpu;
        public bool Dirty = true;
        /// <summary>The batch (D3D9.Draw.cs) that last read the GPU copy: a write after it gets a new GPU buffer.</summary>
        public long ReadInBatch = -1;
        public readonly PrivateData Data = new();
        private uint _priority;

        public Direct3DBuffer9(Direct3DDevice9 device, bool index, uint size, uint usage, uint fvf, int format, int pool)
            : base(device, index ? s_ib : s_vb)
        {
            IsIndex = index; Size = size; Usage = usage; Fvf = fvf; Format = format; Pool = pool;
            _alloc = (Math.Max(size, 4) + 3) & ~3u;
            Cpu = (byte*)NativeMemory.AllocZeroed(_alloc);
        }

        public bool Index32 => Format == D3DFMT_INDEX32;

        protected override void Released()
        {
            if (Gpu != IntPtr.Zero) Device.Retire(Gpu);
            Gpu = IntPtr.Zero;
            NativeMemory.Free(Cpu);
            Data.ReleaseAll();
        }

        /// <summary>The GPU buffer holding the current bytes. A buffer the open batch reads is not written
        /// over: it gets a new GPU buffer (and the old one goes when the batch is submitted).</summary>
        public IntPtr EnsureGpu(long batch)
        {
            if (Dirty || Gpu == IntPtr.Zero)
            {
                var gpu = Device.Device11.Gpu;
                if (Gpu != IntPtr.Zero && ReadInBatch == batch) { Device.Retire(Gpu); Gpu = IntPtr.Zero; }
                if (Gpu == IntPtr.Zero) Gpu = gpu.CreateBuffer(_alloc, WGPUBufferUsage.Vertex | WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
                gpu.WriteBuffer(Gpu, new ReadOnlySpan<byte>(Cpu, (int)_alloc));
                Dirty = false;
            }
            ReadInBatch = batch;
            return Gpu;
        }

        private static Vtable Build(string name, Guid iid)
        {
            var vt = new Vtable(name, 14, iid, Vtbl.IDirect3DResource9.IID);
            FillChild(vt);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, void*, uint, uint, int>)&SetPrivateData);
            vt.Set(5, (delegate* unmanaged<IntPtr, Guid*, void*, uint*, int>)&GetPrivateData);
            vt.Set(6, (delegate* unmanaged<IntPtr, Guid*, int>)&FreePrivateData);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint>)&SetPriority);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint>)&GetPriority);
            vt.Set(9, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&GetType);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, uint, void**, uint, int>)&Lock);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&Unlock);
            vt.Set(13, (delegate* unmanaged<IntPtr, void*, int>)&GetDesc);
            return vt;
        }

        private static Direct3DBuffer9? Self(IntPtr p) => From<Direct3DBuffer9>(p);

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, void* d, uint n, uint flags) => Self(self)?.Data.Set(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, void* d, uint* n) => Self(self)?.Data.Get(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int FreePrivateData(IntPtr self, Guid* g) => Self(self)?.Data.Set(g, 0, null) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static uint SetPriority(IntPtr self, uint p) { var s = Self(self); if (s == null) return 0; uint o = s._priority; s._priority = p; return o; }
        [UnmanagedCallersOnly] private static uint GetPriority(IntPtr self) => Self(self)?._priority ?? 0;
        [UnmanagedCallersOnly] private static void PreLoad(IntPtr self) { }
        [UnmanagedCallersOnly] private static int GetType(IntPtr self) => Self(self)?.IsIndex == true ? D3DRTYPE_INDEXBUFFER : D3DRTYPE_VERTEXBUFFER;

        /// <summary>Lock: the bytes themselves (offset 0 and size 0 lock all of it). The GPU copy is
        /// refreshed at the next draw that reads the buffer.</summary>
        [UnmanagedCallersOnly]
        private static int Lock(IntPtr self, uint offset, uint size, void** data, uint flags)
        {
            var b = Self(self);
            if (b == null || data == null || offset > b.Size) return D3D9Const.D3DERR_INVALIDCALL;
            *data = b.Cpu + offset;
            if ((flags & D3D9Const.D3DLOCK_READONLY) == 0) b.Dirty = true;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int Unlock(IntPtr self) => Self(self) != null ? HResult.S_OK : D3D9Const.D3DERR_INVALIDCALL;

        [UnmanagedCallersOnly]
        private static int GetDesc(IntPtr self, void* desc)
        {
            var b = Self(self);
            if (b == null || desc == null) return D3D9Const.D3DERR_INVALIDCALL;
            if (b.IsIndex) *(D3DINDEXBUFFER_DESC*)desc = new D3DINDEXBUFFER_DESC { Format = b.Format, Type = D3DRTYPE_INDEXBUFFER, Usage = b.Usage, Pool = b.Pool, Size = b.Size };
            else *(D3DVERTEXBUFFER_DESC*)desc = new D3DVERTEXBUFFER_DESC { Format = D3DFMT_VERTEXDATA, Type = D3DRTYPE_VERTEXBUFFER, Usage = b.Usage, Pool = b.Pool, Size = b.Size, FVF = b.Fvf };
            return HResult.S_OK;
        }
    }

    // ---- IDirect3DVertexDeclaration9 ------------------------------------------------------------

    internal sealed unsafe class Direct3DVertexDeclaration9 : D3D9Child
    {
        private static readonly Vtable s_vt = Build();
        public readonly D3DVERTEXELEMENT9[] Elements;   // without the D3DDECL_END terminator
        public readonly uint Fvf;
        public readonly int Id;
        private static int s_ids;

        public const byte FLOAT1 = 0, FLOAT2 = 1, FLOAT3 = 2, FLOAT4 = 3, D3DCOLOR = 4, UBYTE4 = 5, SHORT2 = 6, SHORT4 = 7, UBYTE4N = 8,
            SHORT2N = 9, SHORT4N = 10, USHORT2N = 11, USHORT4N = 12, UDEC3 = 13, DEC3N = 14, FLOAT16_2 = 15, FLOAT16_4 = 16, UNUSED = 17;

        public Direct3DVertexDeclaration9(Direct3DDevice9 device, D3DVERTEXELEMENT9[] elements, uint fvf = 0) : base(device, s_vt)
        {
            Elements = elements;
            Fvf = fvf;
            Id = System.Threading.Interlocked.Increment(ref s_ids);
        }

        public static uint TypeBytes(byte type) => type switch
        {
            FLOAT1 => 4, FLOAT2 => 8, FLOAT3 => 12, FLOAT4 => 16, D3DCOLOR or UBYTE4 or UBYTE4N or SHORT2 or SHORT2N or USHORT2N or UDEC3 or DEC3N or FLOAT16_2 => 4,
            SHORT4 or SHORT4N or USHORT4N or FLOAT16_4 => 8, _ => 0,
        };

        /// <summary>The elements a D3DFVF code stands for, in the order D3D9 lays them out.</summary>
        public static D3DVERTEXELEMENT9[] FromFvf(uint fvf)
        {
            var e = new List<D3DVERTEXELEMENT9>();
            ushort at = 0;
            void Add(byte type, byte usage, byte index = 0)
            {
                e.Add(new D3DVERTEXELEMENT9 { Stream = 0, Offset = at, Type = type, Usage = usage, UsageIndex = index });
                at += (ushort)TypeBytes(type);
            }
            uint pos = fvf & 0x400E;
            int betas = 0;
            switch (pos)
            {
                case 0x2: Add(FLOAT3, D3D9Usage.Position); break;                     // XYZ
                case 0x4: Add(FLOAT4, D3D9Usage.PositionT); break;                    // XYZRHW
                case 0x4002: Add(FLOAT4, D3D9Usage.Position); break;                  // XYZW
                case 0x6: case 0x8: case 0xA: case 0xC: case 0xE:
                    Add(FLOAT3, D3D9Usage.Position);
                    betas = (int)((pos - 0x4) / 2);                                      // XYZB1..XYZB5
                    break;
            }
            if (betas > 0)
            {
                bool lastIndices = (fvf & 0x1000) != 0 || (fvf & 0x8000) != 0;
                int weights = lastIndices ? betas - 1 : betas;
                if (weights > 0) Add((byte)(FLOAT1 + weights - 1), D3D9Usage.BlendWeight);
                if (lastIndices) Add((fvf & 0x8000) != 0 ? D3DCOLOR : UBYTE4, D3D9Usage.BlendIndices);
            }
            if ((fvf & 0x10) != 0) Add(FLOAT3, D3D9Usage.Normal);
            if ((fvf & 0x20) != 0) Add(FLOAT1, D3D9Usage.PSize);
            if ((fvf & 0x40) != 0) Add(D3DCOLOR, D3D9Usage.Color, 0);
            if ((fvf & 0x80) != 0) Add(D3DCOLOR, D3D9Usage.Color, 1);
            int tex = (int)((fvf >> 8) & 0xF);
            for (int i = 0; i < tex && i < 8; i++)
            {
                uint f = (fvf >> (16 + i * 2)) & 3;
                Add(f switch { 0 => FLOAT2, 1 => FLOAT3, 2 => FLOAT4, _ => FLOAT1 }, D3D9Usage.TexCoord, (byte)i);
            }
            return e.ToArray();
        }

        /// <summary>The bytes of one vertex of a D3DFVF code.</summary>
        public static uint FvfStride(uint fvf)
        {
            uint n = 0;
            foreach (var e in FromFvf(fvf)) n = Math.Max(n, e.Offset + TypeBytes(e.Type));
            return n;
        }

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DVertexDeclaration9", Vtbl.IDirect3DVertexDeclaration9.SlotCount, Vtbl.IDirect3DVertexDeclaration9.IID);
            FillChild(vt);
            vt.Set(Vtbl.IDirect3DVertexDeclaration9.GetDeclaration, (delegate* unmanaged<IntPtr, D3DVERTEXELEMENT9*, uint*, int>)&GetDeclaration);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetDeclaration(IntPtr self, D3DVERTEXELEMENT9* elements, uint* count)
        {
            var d = From<Direct3DVertexDeclaration9>(self);
            if (d == null || count == null) return D3D9Const.D3DERR_INVALIDCALL;
            *count = (uint)d.Elements.Length + 1;
            if (elements == null) return HResult.S_OK;
            for (int i = 0; i < d.Elements.Length; i++) elements[i] = d.Elements[i];
            elements[d.Elements.Length] = new D3DVERTEXELEMENT9 { Stream = 0xFF, Offset = 0, Type = UNUSED };
            return HResult.S_OK;
        }
    }

    // ---- IDirect3DVertexShader9 / IDirect3DPixelShader9 -----------------------------------------

    internal sealed unsafe class Direct3DShader9 : D3D9Child
    {
        private static readonly Vtable s_vs = Build("IDirect3DVertexShader9", Vtbl.IDirect3DVertexShader9.IID);
        private static readonly Vtable s_ps = Build("IDirect3DPixelShader9", Vtbl.IDirect3DPixelShader9.IID);
        public readonly D3D9Shader Code;

        public Direct3DShader9(Direct3DDevice9 device, D3D9Shader code) : base(device, code.Pixel ? s_ps : s_vs) => Code = code;

        private readonly Dictionary<int, D3D9Shader> _byStages = new();

        /// <summary>The shader for the textures bound: a ps_1_x shader samples each stage by the type of
        /// the texture there (2D, cube, volume), so it is translated for each combination it meets.</summary>
        public D3D9Shader ForTextures(ComObject?[] textures)
        {
            if (!Code.Pixel || Code.Major != 1) return Code;
            int key = 0;
            var types = new Dictionary<int, int>();
            for (int i = 0; i < 6; i++)
            {
                int t = textures[i] switch { Direct3DTexture9 { IsCube: true } => 3, Direct3DVolumeTexture9 => 4, Direct3DTexture9 => 2, _ => 0 };
                if (t != 0) types[i] = t;
                key |= t << (i * 3);
            }
            lock (_byStages)
            {
                if (!_byStages.TryGetValue(key, out D3D9Shader? s)) _byStages[key] = s = D3D9Bytecode.Translate(Code.Bytecode, pixel: true, types);
                return s;
            }
        }

        private static Vtable Build(string name, Guid iid)
        {
            var vt = new Vtable(name, 5, iid);
            FillChild(vt);
            vt.Set(4, (delegate* unmanaged<IntPtr, byte*, uint*, int>)&GetFunction);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetFunction(IntPtr self, byte* data, uint* size)
        {
            var s = From<Direct3DShader9>(self);
            if (s == null || size == null) return D3D9Const.D3DERR_INVALIDCALL;
            byte[] b = s.Code.Bytecode;
            if (data == null) { *size = (uint)b.Length; return HResult.S_OK; }
            if (*size < b.Length) return D3D9Const.D3DERR_INVALIDCALL;
            b.AsSpan().CopyTo(new Span<byte>(data, b.Length));
            *size = (uint)b.Length;
            return HResult.S_OK;
        }

        /// <summary>The length of a shader's bytecode: up to and including its end token.</summary>
        public static int Length(uint* tokens)
        {
            int i = 1;
            while (true)
            {
                uint t = tokens[i];
                if ((t & 0xFFFF) == 0xFFFF) return (i + 1) * 4;
                if ((t & 0xFFFF) == 0xFFFE) { i += 1 + (int)((t >> 16) & 0x7FFF); continue; }
                i++;
                if (i > 1 << 20) throw new ArgumentException("shader bytecode without an end token");
            }
        }
    }

    // ---- IDirect3DQuery9 ------------------------------------------------------------------------

    internal sealed unsafe class Direct3DQuery9 : D3D9Child
    {
        private static readonly Vtable s_vt = Build();
        public const int D3DQUERYTYPE_EVENT = 8, D3DQUERYTYPE_OCCLUSION = 9, D3DQUERYTYPE_TIMESTAMP = 10, D3DQUERYTYPE_TIMESTAMPDISJOINT = 11, D3DQUERYTYPE_TIMESTAMPFREQ = 12;
        public readonly int Type;
        private bool _issued;
        private readonly OcclusionQuery _samples = new();

        public Direct3DQuery9(Direct3DDevice9 device, int type) : base(device, s_vt) => Type = type;

        public static bool Supported(int type) => type is D3DQUERYTYPE_EVENT or D3DQUERYTYPE_OCCLUSION or D3DQUERYTYPE_TIMESTAMP or D3DQUERYTYPE_TIMESTAMPDISJOINT or D3DQUERYTYPE_TIMESTAMPFREQ;

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DQuery9", Vtbl.IDirect3DQuery9.SlotCount, Vtbl.IDirect3DQuery9.IID);
            FillChild(vt);
            vt.Set(Vtbl.IDirect3DQuery9.GetType, (delegate* unmanaged<IntPtr, int>)&GetType);
            vt.Set(Vtbl.IDirect3DQuery9.GetDataSize, (delegate* unmanaged<IntPtr, uint>)&GetDataSize);
            vt.Set(Vtbl.IDirect3DQuery9.Issue, (delegate* unmanaged<IntPtr, uint, int>)&Issue);
            vt.Set(Vtbl.IDirect3DQuery9.GetData, (delegate* unmanaged<IntPtr, void*, uint, uint, int>)&GetData);
            return vt;
        }

        private static Direct3DQuery9? Self(IntPtr p) => From<Direct3DQuery9>(p);

        [UnmanagedCallersOnly] private static int GetType(IntPtr self) => Self(self)?.Type ?? 0;
        [UnmanagedCallersOnly] private static uint GetDataSize(IntPtr self) => Self(self)?.Type switch { D3DQUERYTYPE_EVENT or D3DQUERYTYPE_TIMESTAMPDISJOINT or D3DQUERYTYPE_OCCLUSION => 4u, _ => 8u };

        [UnmanagedCallersOnly]
        private static int Issue(IntPtr self, uint flags)
        {
            var q = Self(self);
            if (q == null) return D3D9Const.D3DERR_INVALIDCALL;
            if (q.Type == D3DQUERYTYPE_OCCLUSION)
            {
                // D3DISSUE_BEGIN (2) starts counting, D3DISSUE_END (1) stops
                if ((flags & 2) != 0) { q._issued = false; q.Device.BeginOcclusion(q._samples); }
                if ((flags & 1) != 0) { q.Device.EndOcclusion(q._samples); q._issued = true; }
                return HResult.S_OK;
            }
            if ((flags & 1) != 0) { q._issued = true; q.Device.FlushDraws(); }     // D3DISSUE_END: what came before is sent
            return HResult.S_OK;
        }

        /// <summary>GetData: an event is signalled once what was issued before it is submitted (the device
        /// flushed at Issue); timestamps count milliseconds of the process clock, at 1 GHz.</summary>
        [UnmanagedCallersOnly]
        private static int GetData(IntPtr self, void* data, uint size, uint flags)
        {
            var q = Self(self);
            if (q == null) return D3D9Const.D3DERR_INVALIDCALL;
            if (!q._issued && q.Type == D3DQUERYTYPE_EVENT) return HResult.S_FALSE;
            if (q.Type == D3DQUERYTYPE_OCCLUSION)
            {
                if (!q._issued) return D3D9Const.D3DERR_INVALIDCALL;
                // the samples: in work still recording, submitted first (D3DGETDATA_FLUSH or not: it is cheap here)
                var t = q.Device.Occlusion;
                if (t != null && q._samples.InCurrentBatch(t)) q.Device.FlushDraws();
                uint samples = t == null ? 0 : (uint)Math.Min(t.Result(q._samples), uint.MaxValue);
                if (data != null && size >= 4) *(uint*)data = samples;
                return HResult.S_OK;
            }
            if (data == null || size == 0) return HResult.S_OK;
            switch (q.Type)
            {
                case D3DQUERYTYPE_EVENT: if (size >= 4) *(int*)data = 1; break;
                case D3DQUERYTYPE_TIMESTAMPDISJOINT: if (size >= 4) *(int*)data = 0; break;
                case D3DQUERYTYPE_TIMESTAMPFREQ: if (size >= 8) *(ulong*)data = 1_000_000_000UL; break;
                case D3DQUERYTYPE_TIMESTAMP: if (size >= 8) *(ulong*)data = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e9 / System.Diagnostics.Stopwatch.Frequency)); break;
            }
            return HResult.S_OK;
        }
    }

    // ---- IDirect3DStateBlock9 -------------------------------------------------------------------

    internal sealed unsafe class Direct3DStateBlock9 : D3D9Child
    {
        private static readonly Vtable s_vt = Build();
        /// <summary>The states the block holds, and their values (D3D9State's keys).</summary>
        public readonly Dictionary<long, object?> Values = new();

        public Direct3DStateBlock9(Direct3DDevice9 device) : base(device, s_vt) { }

        protected override void Released()
        {
            foreach (object? v in Values.Values) if (v is ComObject o) o.InternalRelease();
            Values.Clear();
        }

        public void Set(long key, object? value)
        {
            if (Values.TryGetValue(key, out object? old) && old is ComObject o) o.InternalRelease();
            if (value is ComObject n) n.InternalAddRef();
            Values[key] = value;
        }

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DStateBlock9", Vtbl.IDirect3DStateBlock9.SlotCount, Vtbl.IDirect3DStateBlock9.IID);
            FillChild(vt);
            vt.Set(Vtbl.IDirect3DStateBlock9.Capture, (delegate* unmanaged<IntPtr, int>)&Capture);
            vt.Set(Vtbl.IDirect3DStateBlock9.Apply, (delegate* unmanaged<IntPtr, int>)&Apply);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int Capture(IntPtr self)
        {
            var b = From<Direct3DStateBlock9>(self);
            if (b == null) return D3D9Const.D3DERR_INVALIDCALL;
            foreach (long k in new List<long>(b.Values.Keys)) b.Set(k, b.Device.State.Get(k));
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Apply(IntPtr self)
        {
            var b = From<Direct3DStateBlock9>(self);
            if (b == null) return D3D9Const.D3DERR_INVALIDCALL;
            foreach (var (k, v) in b.Values) b.Device.State.Set(k, v);
            return HResult.S_OK;
        }
    }
}
