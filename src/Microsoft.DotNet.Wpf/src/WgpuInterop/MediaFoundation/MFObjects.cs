// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation off Windows: media types, samples, buffers (of memory, and of a Direct3D 11 texture
// of the shim's: IMFDXGIBuffer), the DXGI device manager and the activation objects MFTEnumEx returns.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static unsafe class Com
    {
        public static uint AddRef(IntPtr p) => p == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)p)[1])(p);
        public static uint Release(IntPtr p) => p == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)p)[2])(p);
        public static int QueryInterface(IntPtr p, in Guid iid, out IntPtr ppv)
        {
            IntPtr r = IntPtr.Zero;
            Guid g = iid;
            int hr = p == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)p)[0])(p, &g, &r);
            ppv = r;
            return hr;
        }
        public static IntPtr Fn(IntPtr p, int slot) => (*(IntPtr**)p)[slot];
    }

    // ---- media types -------------------------------------------------------------------------------

    internal sealed unsafe class MFMediaType : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaType", 38, MF.IID_IMFMediaType, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            vt.Set(34, (delegate* unmanaged<IntPtr, int*, int>)&IsCompressedFormat);
            vt.Set(35, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&IsEqual);
#if WGPU_BROWSER
            // WebAssembly's C ABI passes the GUID by reference (and its interpreter makes no wrapper of a struct by value)
            vt.Set(36, (delegate* unmanaged<IntPtr, Guid*, void**, int>)&GetRepresentation);
            vt.Set(37, (delegate* unmanaged<IntPtr, Guid*, void*, int>)&FreeRepresentation);
#else
            vt.Set(36, (delegate* unmanaged<IntPtr, Guid, void**, int>)&GetRepresentation);
            vt.Set(37, (delegate* unmanaged<IntPtr, Guid, void*, int>)&FreeRepresentation);
#endif
            return vt;
        }

        public MFMediaType() : base(s_vt) { }

        /// <summary>A video type: major type, subtype, the frame's size.</summary>
        public static MFMediaType Video(Guid subtype, uint width = 0, uint height = 0)
        {
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, subtype);
            if (width > 0 && height > 0) t.Attributes.Set(MF.MF_MT_FRAME_SIZE, MF.Pack(width, height));
            return t;
        }

        public Guid Subtype => Attributes.Guid(MF.MF_MT_SUBTYPE);
        public (uint W, uint H) FrameSize => MF.Unpack(Attributes.U64(MF.MF_MT_FRAME_SIZE));

        [UnmanagedCallersOnly]
        private static int GetMajorType(IntPtr self, Guid* g)
        {
            if (From<MFMediaType>(self) is not { } t || g == null) return MF.E_POINTER;
            if (t.Attributes.Get(MF.MF_MT_MAJOR_TYPE) is not { Type: MF.VT_CLSID } v) return MF.MF_E_ATTRIBUTENOTFOUND;
            *g = v.G;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int IsCompressedFormat(IntPtr self, int* c)
        {
            if (From<MFMediaType>(self) is not { } t || c == null) return MF.E_POINTER;
            // MF_MT_COMPRESSED where set; else what MF_MT_ALL_SAMPLES_INDEPENDENT does not say
            *c = t.Attributes.Get(MF.MF_MT_COMPRESSED) is { Type: MF.VT_UI4 } v ? (v.U != 0 ? 1 : 0) : (t.Attributes.U32(MF.MF_MT_ALL_SAMPLES_INDEPENDENT) != 0 ? 0 : 1);
            return MF.S_OK;
        }

        /// <summary>MF_MEDIATYPE_EQUAL_* flags: major types 1, subtypes 2, format data 4, user data 8.</summary>
        [UnmanagedCallersOnly]
        private static int IsEqual(IntPtr self, IntPtr other, uint* flags)
        {
            if (From<MFMediaType>(self) is not { } a || flags == null) return MF.E_POINTER;
            if (From<MFMediaType>(other) is not { } b) return MF.E_INVALIDARG;
            uint f = 0;
            if (a.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == b.Attributes.Guid(MF.MF_MT_MAJOR_TYPE)) f |= 1;
            if (a.Attributes.Guid(MF.MF_MT_SUBTYPE) == b.Attributes.Guid(MF.MF_MT_SUBTYPE)) f |= 2;
            static bool Data(MFAttributeStore x, MFAttributeStore y)
            {
                foreach (var (k, v) in x.All())
                    if (k != MF.MF_MT_USER_DATA && !(y.Get(k) is { } w && w.SameAs(v))) return false;
                return true;
            }
            if (Data(a.Attributes, b.Attributes) && Data(b.Attributes, a.Attributes)) f |= 4;
            var ua = a.Attributes.Get(MF.MF_MT_USER_DATA); var ub = b.Attributes.Get(MF.MF_MT_USER_DATA);
            if ((ua == null && ub == null) || (ua != null && ub != null && ua.SameAs(ub))) f |= 8;
            *flags = f;
            return f == 0xF ? MF.S_OK : MF.S_FALSE;
        }

#if WGPU_BROWSER
        [UnmanagedCallersOnly] private static int GetRepresentation(IntPtr self, Guid* rep, void** p) { if (p != null) *p = null; return MF.MF_E_UNSUPPORTED_REPRESENTATION; }
        [UnmanagedCallersOnly] private static int FreeRepresentation(IntPtr self, Guid* rep, void* p) => MF.S_OK;
#else
        [UnmanagedCallersOnly] private static int GetRepresentation(IntPtr self, Guid rep, void** p) { if (p != null) *p = null; return MF.MF_E_UNSUPPORTED_REPRESENTATION; }
        [UnmanagedCallersOnly] private static int FreeRepresentation(IntPtr self, Guid rep, void* p) => MF.S_OK;
#endif

        /// <summary>A copy, for handing out (GetOutputAvailableType and the like).</summary>
        public MFMediaType Clone()
        {
            var c = new MFMediaType();
            foreach (var (k, v) in Attributes.All()) c.Attributes.Set(k, Copy(v));
            return c;
        }
    }

    // ---- buffers -------------------------------------------------------------------------------------

    /// <summary>An IMFMediaBuffer; a 2-D one (IMF2DBuffer2) where it holds a picture.</summary>
    internal abstract unsafe class MFBuffer : ComObject
    {
        protected MFBuffer(params Vtable[] vts) : base(vts) { }

        public uint CurrentLength, MaxLength;

        public abstract byte* LockBytes(bool write);
        public abstract void UnlockBytes();

        protected static void FillBuffer(Vtable vt)
        {
            vt.Set(3, (delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)&Lock);
            vt.Set(4, (delegate* unmanaged<IntPtr, int>)&Unlock);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint*, int>)&GetCurrentLength);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, int>)&SetCurrentLength);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, int>)&GetMaxLength);
        }

        [UnmanagedCallersOnly]
        private static int Lock(IntPtr self, byte** p, uint* max, uint* current)
        {
            if (From<MFBuffer>(self) is not { } b || p == null) return MF.E_POINTER;
            try { *p = b.LockBytes(true); }
            catch (Exception e) { D3DLog.Error("IMFMediaBuffer.Lock", e); return MF.E_FAIL; }
            if (max != null) *max = b.MaxLength;
            if (current != null) *current = b.CurrentLength;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int Unlock(IntPtr self) { From<MFBuffer>(self)?.UnlockBytes(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetCurrentLength(IntPtr self, uint* n) { if (From<MFBuffer>(self) is not { } b || n == null) return MF.E_POINTER; *n = b.CurrentLength; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int SetCurrentLength(IntPtr self, uint n)
        {
            if (From<MFBuffer>(self) is not { } b) return MF.E_POINTER;
            if (n > b.MaxLength) return MF.E_INVALIDARG;
            b.CurrentLength = n;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetMaxLength(IntPtr self, uint* n) { if (From<MFBuffer>(self) is not { } b || n == null) return MF.E_POINTER; *n = b.MaxLength; return MF.S_OK; }
    }

    /// <summary>A buffer of system memory (MFCreateMemoryBuffer, MFCreateAlignedMemoryBuffer).</summary>
    internal sealed unsafe class MFMemoryBuffer : MFBuffer
    {
        private static readonly Vtable s_vt = Build();
        private static Vtable Build() { var vt = new Vtable("IMFMediaBuffer", 8, MF.IID_IMFMediaBuffer); FillBuffer(vt); return vt; }

        private byte* _data;

        /// <summary>A buffer of <paramref name="size"/> bytes, aligned to the mask's (MF_16_BYTE_ALIGNMENT is 15) or 16 bytes.</summary>
        public MFMemoryBuffer(uint size, uint alignmentMask = 15) : base(s_vt)
        {
            MaxLength = size;
            nuint align = (nuint)Math.Max(16, System.Numerics.BitOperations.RoundUpToPowerOf2(alignmentMask + 1));
            _data = (byte*)NativeMemory.AlignedAlloc(Math.Max(size, 1), align);
        }

        public override byte* LockBytes(bool write) => _data;
        public override void UnlockBytes() { }

        protected override void OnFinalRelease()
        {
            if (_data != null) NativeMemory.AlignedFree(_data);
            _data = null;
        }
    }

    /// <summary>
    /// A buffer over a subresource of a Direct3D 11 texture of the shim's (MFCreateDXGISurfaceBuffer):
    /// IMFDXGIBuffer for the texture itself; locked, the texture's texels in system memory -- of an NV12
    /// texture its luma rows and then its chroma rows, of the texture's width each -- read back through
    /// a staging texture and written back where the lock was for writing.
    /// </summary>
    internal sealed unsafe class MFDxgiBuffer : MFBuffer
    {
        private static readonly Vtable s_buffer = BuildBuffer(), s_2d = Build2D(), s_dxgi = BuildDxgi();

        private static Vtable BuildBuffer() { var vt = new Vtable("IMFMediaBuffer", 8, MF.IID_IMFMediaBuffer); FillBuffer(vt); return vt; }

        private static Vtable Build2D()
        {
            var vt = new Vtable("IMF2DBuffer2", 12, MF.IID_IMF2DBuffer2, MF.IID_IMF2DBuffer);
            vt.Set(3, (delegate* unmanaged<IntPtr, byte**, int*, int>)&Lock2D);
            vt.Set(4, (delegate* unmanaged<IntPtr, int>)&Unlock2D);
            vt.Set(5, (delegate* unmanaged<IntPtr, byte**, int*, int>)&GetScanline0AndPitch);
            vt.Set(6, (delegate* unmanaged<IntPtr, int*, int>)&IsContiguousFormat);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, int>)&GetContiguousLength);
            vt.Set(8, (delegate* unmanaged<IntPtr, byte*, uint, int>)&ContiguousCopyTo);
            vt.Set(9, (delegate* unmanaged<IntPtr, byte*, uint, int>)&ContiguousCopyFrom);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, byte**, int*, byte**, uint*, int>)&Lock2DSize);
            vt.Set(11, (delegate* unmanaged<IntPtr, IntPtr, int>)&Copy2DTo);
            return vt;
        }

        private static Vtable BuildDxgi()
        {
            var vt = new Vtable("IMFDXGIBuffer", 7, MF.IID_IMFDXGIBuffer);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&GetResource);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetSubresourceIndex);
            vt.Set(5, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetUnknownData);
            vt.Set(6, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetUnknownData);
            return vt;
        }

        public readonly IntPtr Texture;            // ID3D11Texture2D, AddRef'd
        public readonly uint Subresource;
        private readonly D3DTexture2D _tex;
        private byte* _cpu;
        private int _locks;
        private bool _written;
        private readonly Dictionary<Guid, IntPtr> _data = new();

        public MFDxgiBuffer(IntPtr texture, uint subresource) : base(s_buffer, s_2d, s_dxgi)
        {
            _tex = ComObject.From<D3DTexture2D>(texture) ?? throw new ArgumentException("not a texture of the shim's");
            Texture = texture;
            Com.AddRef(texture);
            Subresource = subresource;
            MaxLength = CurrentLength = (uint)ContiguousLength;
        }

        private uint W => _tex.Desc.Width;
        private uint H => _tex.Desc.Height;
        /// <summary>NV12, P010, P016: the luma rows, then half as many rows of interleaved chroma.</summary>
        private bool Planar => Formats.IsPlanar(_tex.Desc.Format);
        private int Pitch => (int)W * (int)Formats.TexelBytes(_tex.Desc.Format);
        private int Rows => Planar ? (int)(H + H / 2) : (int)H;
        private int ContiguousLength => Pitch * Rows;

        public override byte* LockBytes(bool write)
        {
            if (Interlocked.Increment(ref _locks) == 1)
            {
                _cpu = (byte*)NativeMemory.Alloc((nuint)ContiguousLength);
                D3DTextureIo.Read(Texture, Subresource, _cpu, Pitch, Rows);
                _written = write;
            }
            else _written |= write;
            return _cpu;
        }

        public override void UnlockBytes()
        {
            if (_locks == 0 || Interlocked.Decrement(ref _locks) != 0) return;
            if (_written) D3DTextureIo.Write(Texture, Subresource, _cpu, Pitch, Rows);
            NativeMemory.Free(_cpu);
            _cpu = null;
        }

        protected override void OnFinalRelease()
        {
            if (_cpu != null) NativeMemory.Free(_cpu);
            foreach (var p in _data.Values) Com.Release(p);
            Com.Release(Texture);
        }

        [UnmanagedCallersOnly]
        private static int Lock2D(IntPtr self, byte** scan0, int* pitch)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || scan0 == null || pitch == null) return MF.E_POINTER;
            *scan0 = b.LockBytes(true);
            *pitch = b.Pitch;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int Unlock2D(IntPtr self) { From<MFDxgiBuffer>(self)?.UnlockBytes(); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetScanline0AndPitch(IntPtr self, byte** scan0, int* pitch)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || scan0 == null || pitch == null) return MF.E_POINTER;
            if (b._cpu == null) return MF.E_FAIL;           // not locked (MF_E_UNEXPECTED on Windows)
            *scan0 = b._cpu;
            *pitch = b.Pitch;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int IsContiguousFormat(IntPtr self, int* c) { if (c == null) return MF.E_POINTER; *c = 1; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetContiguousLength(IntPtr self, uint* n) { if (From<MFDxgiBuffer>(self) is not { } b || n == null) return MF.E_POINTER; *n = (uint)b.ContiguousLength; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int ContiguousCopyTo(IntPtr self, byte* dst, uint size)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || dst == null) return MF.E_POINTER;
            if (size < b.ContiguousLength) return MF.MF_E_BUFFERTOOSMALL;
            D3DTextureIo.Read(b.Texture, b.Subresource, dst, b.Pitch, b.Rows);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ContiguousCopyFrom(IntPtr self, byte* src, uint size)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || src == null) return MF.E_POINTER;
            if (size < b.ContiguousLength) return MF.E_INVALIDARG;
            D3DTextureIo.Write(b.Texture, b.Subresource, src, b.Pitch, b.Rows);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Lock2DSize(IntPtr self, uint flags, byte** scan0, int* pitch, byte** start, uint* length)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || scan0 == null || pitch == null) return MF.E_POINTER;
            // MF2DBuffer_LockFlags_Read 1, Write 2, ReadWrite 3
            *scan0 = b.LockBytes((flags & 2) != 0);
            *pitch = b.Pitch;
            if (start != null) *start = *scan0;
            if (length != null) *length = (uint)b.ContiguousLength;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Copy2DTo(IntPtr self, IntPtr dest)
        {
            if (From<MFDxgiBuffer>(self) is not { } b) return MF.E_POINTER;
            if (From<MFBuffer>(dest) is not { } d) return MF.E_INVALIDARG;
            byte* to = d.LockBytes(true);
            try
            {
                if (d.MaxLength < b.ContiguousLength) return MF.MF_E_BUFFERTOOSMALL;
                D3DTextureIo.Read(b.Texture, b.Subresource, to, b.Pitch, b.Rows);
                d.CurrentLength = (uint)b.ContiguousLength;
            }
            finally { d.UnlockBytes(); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetResource(IntPtr self, Guid* riid, IntPtr* ppv)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || riid == null || ppv == null) return MF.E_POINTER;
            int hr = Com.QueryInterface(b.Texture, *riid, out var p);
            *ppv = p;
            return hr;
        }

        [UnmanagedCallersOnly] private static int GetSubresourceIndex(IntPtr self, uint* i) { if (From<MFDxgiBuffer>(self) is not { } b || i == null) return MF.E_POINTER; *i = b.Subresource; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetUnknownData(IntPtr self, Guid* g, Guid* riid, IntPtr* ppv)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || g == null || riid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            lock (b._data)
            {
                if (!b._data.TryGetValue(*g, out var u)) return MF.MF_E_ATTRIBUTENOTFOUND;   // MF_E_NOT_FOUND
                int hr = Com.QueryInterface(u, *riid, out var p);
                *ppv = p;
                return hr;
            }
        }

        [UnmanagedCallersOnly]
        private static int SetUnknownData(IntPtr self, Guid* g, IntPtr u)
        {
            if (From<MFDxgiBuffer>(self) is not { } b || g == null) return MF.E_POINTER;
            lock (b._data)
            {
                if (b._data.Remove(*g, out var old)) Com.Release(old);
                if (u != IntPtr.Zero) { Com.AddRef(u); b._data[*g] = u; }
            }
            return MF.S_OK;
        }
    }

    // ---- samples ----------------------------------------------------------------------------------

    internal sealed unsafe class MFSample : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFSample", 47, MF.IID_IMFSample, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, uint*, int>)&GetSampleFlags);
            vt.Set(34, (delegate* unmanaged<IntPtr, uint, int>)&SetSampleFlags);
            vt.Set(35, (delegate* unmanaged<IntPtr, long*, int>)&GetSampleTime);
            vt.Set(36, (delegate* unmanaged<IntPtr, long, int>)&SetSampleTime);
            vt.Set(37, (delegate* unmanaged<IntPtr, long*, int>)&GetSampleDuration);
            vt.Set(38, (delegate* unmanaged<IntPtr, long, int>)&SetSampleDuration);
            vt.Set(39, (delegate* unmanaged<IntPtr, uint*, int>)&GetBufferCount);
            vt.Set(40, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetBufferByIndex);
            vt.Set(41, (delegate* unmanaged<IntPtr, IntPtr*, int>)&ConvertToContiguousBuffer);
            vt.Set(42, (delegate* unmanaged<IntPtr, IntPtr, int>)&AddBuffer);
            vt.Set(43, (delegate* unmanaged<IntPtr, uint, int>)&RemoveBufferByIndex);
            vt.Set(44, (delegate* unmanaged<IntPtr, int>)&RemoveAllBuffers);
            vt.Set(45, (delegate* unmanaged<IntPtr, uint*, int>)&GetTotalLength);
            vt.Set(46, (delegate* unmanaged<IntPtr, IntPtr, int>)&CopyToBuffer);
            return vt;
        }

        public MFSample() : base(s_vt) { }

        public uint Flags;
        public long? Time, Duration;
        /// <summary>IMFMediaBuffer pointers, AddRef'd.</summary>
        public readonly List<IntPtr> Buffers = new();
        /// <summary>A sample of a decoder's pool: handed back to it when the application lets it go.</summary>
        public Action<MFSample>? ReturnTo;

        protected override bool OnNoReferences()
        {
            if (ReturnTo is { } back) { back(this); return true; }
            return false;
        }

        protected override void OnFinalRelease()
        {
            lock (Buffers) { foreach (var b in Buffers) Com.Release(b); Buffers.Clear(); }
            base.OnFinalRelease();
        }

        public void SetBuffer(IntPtr buffer)
        {
            lock (Buffers)
            {
                foreach (var b in Buffers) Com.Release(b);
                Buffers.Clear();
                Com.AddRef(buffer);
                Buffers.Add(buffer);
            }
        }

        /// <summary>The bytes of all the buffers, in order (their current lengths).</summary>
        public byte[] Bytes()
        {
            var r = new List<byte>();
            lock (Buffers)
                foreach (var p in Buffers)
                {
                    if (From<MFBuffer>(p) is { } b)
                    {
                        byte* d = b.LockBytes(false);
                        try { r.AddRange(new ReadOnlySpan<byte>(d, (int)b.CurrentLength).ToArray()); }
                        finally { b.UnlockBytes(); }
                    }
                    else r.AddRange(ForeignBytes(p));
                }
            return r.ToArray();
        }

        /// <summary>The bytes of a sample not of ours, through its own IMFSample methods.</summary>
        internal static byte[] ForeignSampleBytes(IntPtr sample)
        {
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sample, 39))(sample, &n);
            var all = new List<byte>();
            for (uint i = 0; i < n; i++)
            {
                IntPtr b;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sample, 40))(sample, i, &b) < 0) continue;
                all.AddRange(From<MFBuffer>(b) is { } ours ? MFVideoDecoder.OwnBytes(ours) : MFSample.ForeignBytes(b));
                Com.Release(b);
            }
            return all.ToArray();
        }

        /// <summary>A buffer not of ours: through its own Lock and Unlock.</summary>
        internal static byte[] ForeignBytes(IntPtr p)
        {
            byte* d; uint max, cur;
            if (((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Com.Fn(p, 3))(p, &d, &max, &cur) < 0) return Array.Empty<byte>();
            try { return new ReadOnlySpan<byte>(d, (int)cur).ToArray(); }
            finally { ((delegate* unmanaged<IntPtr, int>)Com.Fn(p, 4))(p); }
        }

        [UnmanagedCallersOnly] private static int GetSampleFlags(IntPtr self, uint* f) { if (From<MFSample>(self) is not { } s || f == null) return MF.E_POINTER; *f = s.Flags; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetSampleFlags(IntPtr self, uint f) { if (From<MFSample>(self) is { } s) s.Flags = f; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetSampleTime(IntPtr self, long* t) { if (From<MFSample>(self) is not { } s || t == null) return MF.E_POINTER; if (s.Time is not { } v) return MF.MF_E_NOSAMPLETIMESTAMP; *t = v; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetSampleTime(IntPtr self, long t) { if (From<MFSample>(self) is { } s) s.Time = t; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetSampleDuration(IntPtr self, long* t) { if (From<MFSample>(self) is not { } s || t == null) return MF.E_POINTER; if (s.Duration is not { } v) return MF.MF_E_NO_SAMPLE_DURATION; *t = v; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetSampleDuration(IntPtr self, long t) { if (From<MFSample>(self) is { } s) s.Duration = t; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetBufferCount(IntPtr self, uint* n) { if (From<MFSample>(self) is not { } s || n == null) return MF.E_POINTER; lock (s.Buffers) *n = (uint)s.Buffers.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetBufferByIndex(IntPtr self, uint i, IntPtr* p)
        {
            if (From<MFSample>(self) is not { } s || p == null) return MF.E_POINTER;
            lock (s.Buffers)
            {
                if (i >= s.Buffers.Count) return MF.E_INVALIDARG;
                Com.AddRef(s.Buffers[(int)i]);
                *p = s.Buffers[(int)i];
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ConvertToContiguousBuffer(IntPtr self, IntPtr* p)
        {
            if (From<MFSample>(self) is not { } s || p == null) return MF.E_POINTER;
            lock (s.Buffers)
            {
                if (s.Buffers.Count == 1) { Com.AddRef(s.Buffers[0]); *p = s.Buffers[0]; return MF.S_OK; }
            }
            // several (or none): one buffer of their bytes, the sample's from now on
            var bytes = s.Bytes();
            var mem = new MFMemoryBuffer((uint)bytes.Length);
            byte* d = mem.LockBytes(true);
            bytes.CopyTo(new Span<byte>(d, bytes.Length));
            mem.CurrentLength = (uint)bytes.Length;
            s.SetBuffer(mem.Pointer);
            mem.Release();
            *p = mem.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int AddBuffer(IntPtr self, IntPtr b)
        {
            if (From<MFSample>(self) is not { } s || b == IntPtr.Zero) return MF.E_POINTER;
            Com.AddRef(b);
            lock (s.Buffers) s.Buffers.Add(b);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RemoveBufferByIndex(IntPtr self, uint i)
        {
            if (From<MFSample>(self) is not { } s) return MF.E_POINTER;
            lock (s.Buffers)
            {
                if (i >= s.Buffers.Count) return MF.E_INVALIDARG;
                Com.Release(s.Buffers[(int)i]);
                s.Buffers.RemoveAt((int)i);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int RemoveAllBuffers(IntPtr self) { if (From<MFSample>(self) is { } s) lock (s.Buffers) { foreach (var b in s.Buffers) Com.Release(b); s.Buffers.Clear(); } return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetTotalLength(IntPtr self, uint* n)
        {
            if (From<MFSample>(self) is not { } s || n == null) return MF.E_POINTER;
            uint total = 0;
            lock (s.Buffers)
                foreach (var p in s.Buffers)
                {
                    uint cur;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(p, 5))(p, &cur);
                    total += cur;
                }
            *n = total;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CopyToBuffer(IntPtr self, IntPtr dest)
        {
            if (From<MFSample>(self) is not { } s || dest == IntPtr.Zero) return MF.E_POINTER;
            var bytes = s.Bytes();
            byte* d; uint max, cur;
            int hr = ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Com.Fn(dest, 3))(dest, &d, &max, &cur);
            if (hr < 0) return hr;
            try
            {
                if (max < bytes.Length) return MF.MF_E_BUFFERTOOSMALL;
                bytes.CopyTo(new Span<byte>(d, bytes.Length));
            }
            finally { ((delegate* unmanaged<IntPtr, int>)Com.Fn(dest, 4))(dest); }
            ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(dest, 6))(dest, (uint)bytes.Length);
            return MF.S_OK;
        }
    }

    // ---- the DXGI device manager --------------------------------------------------------------------

    internal sealed unsafe class MFDeviceManager : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private static int s_tokens;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFDXGIDeviceManager", 10, MF.IID_IMFDXGIDeviceManager);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, int>)&CloseDeviceHandle);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)&GetVideoService);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int, int>)&LockDevice);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr*, int>)&OpenDeviceHandle);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr, uint, int>)&ResetDevice);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr, int>)&TestDevice);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int, int>)&UnlockDevice);
            return vt;
        }

        public readonly uint Token = (uint)Interlocked.Increment(ref s_tokens);
        public IntPtr Device;          // ID3D11Device, AddRef'd
        private int _generation;
        private readonly object _lock = new();

        public MFDeviceManager() : base(s_vt) { }

        protected override void OnFinalRelease() => Com.Release(Device);

        // a device handle: the generation of the device it was opened on (a reset invalidates it)
        private bool Valid(IntPtr h) => Device != IntPtr.Zero && h == (IntPtr)_generation;

        [UnmanagedCallersOnly] private static int CloseDeviceHandle(IntPtr self, IntPtr h) => MF.S_OK;

        [UnmanagedCallersOnly]
        private static int GetVideoService(IntPtr self, IntPtr h, Guid* riid, IntPtr* ppv)
        {
            if (From<MFDeviceManager>(self) is not { } m || riid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            if (m.Device == IntPtr.Zero) return MF.MF_E_NOT_INITIALIZED;
            if (!m.Valid(h)) return unchecked((int)0x80041002);         // MF_E_DXGI_NEW_VIDEO_DEVICE
            int hr = Com.QueryInterface(m.Device, *riid, out var p);
            *ppv = p;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int LockDevice(IntPtr self, IntPtr h, Guid* riid, IntPtr* ppv, int block)
        {
            if (From<MFDeviceManager>(self) is not { } m || riid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            if (m.Device == IntPtr.Zero) return MF.MF_E_NOT_INITIALIZED;
            if (!m.Valid(h)) return unchecked((int)0x80041002);
            int hr = Com.QueryInterface(m.Device, *riid, out var p);
            *ppv = p;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int OpenDeviceHandle(IntPtr self, IntPtr* h)
        {
            if (From<MFDeviceManager>(self) is not { } m || h == null) return MF.E_POINTER;
            if (m.Device == IntPtr.Zero) return MF.MF_E_NOT_INITIALIZED;
            *h = (IntPtr)m._generation;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ResetDevice(IntPtr self, IntPtr device, uint token)
        {
            if (From<MFDeviceManager>(self) is not { } m) return MF.E_POINTER;
            if (token != m.Token || device == IntPtr.Zero) return MF.E_INVALIDARG;
            int hr = Com.QueryInterface(device, new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), out var d3d11);     // ID3D11Device
            if (hr < 0) return MF.E_INVALIDARG;
            lock (m._lock)
            {
                Com.Release(m.Device);
                m.Device = d3d11;
                m._generation++;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int TestDevice(IntPtr self, IntPtr h) => From<MFDeviceManager>(self) is { } m && m.Valid(h) ? MF.S_OK : unchecked((int)0x80041002);
        [UnmanagedCallersOnly] private static int UnlockDevice(IntPtr self, IntPtr h, int save) => MF.S_OK;

        /// <summary>The device as the shim's D3DDevice.</summary>
        public D3DDevice? D3D => From<D3DDevice>(Device);
    }

    // ---- activation ------------------------------------------------------------------------------------

    /// <summary>What MFTEnumEx returns: a transform's attributes (its name, its CLSID, its types) and the means to make it.</summary>
    internal sealed unsafe class MFActivate : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFActivate", 36, MF.IID_IMFActivate, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&ActivateObject);
            vt.Set(34, (delegate* unmanaged<IntPtr, int>)&ShutdownObject);
            vt.Set(35, (delegate* unmanaged<IntPtr, int>)&DetachObject);
            return vt;
        }

        private readonly Func<ComObject> _make;
        private ComObject? _made;

        public MFActivate(Func<ComObject> make) : base(s_vt) { _make = make; }

        protected override void OnFinalRelease()
        {
            _made?.Release();
            _made = null;
            base.OnFinalRelease();
        }

        [UnmanagedCallersOnly]
        private static int ActivateObject(IntPtr self, Guid* riid, IntPtr* ppv)
        {
            if (From<MFActivate>(self) is not { } a || riid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            try { a._made ??= a._make(); }
            catch (Exception e) { D3DLog.Error("IMFActivate.ActivateObject", e); return MF.E_FAIL; }
            int hr = Com.QueryInterface(a._made.Pointer, *riid, out var p);
            *ppv = p;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int ShutdownObject(IntPtr self)
        {
            if (From<MFActivate>(self) is { } a && a._made is { } m) { (m as MFTransform)?.Shutdown(); m.Release(); a._made = null; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int DetachObject(IntPtr self)
        {
            if (From<MFActivate>(self) is { } a && a._made is { } m) { m.Release(); a._made = null; }
            return MF.S_OK;
        }
    }
}
