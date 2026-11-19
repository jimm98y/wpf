// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The color converter DSP's IMediaObject (the DMO it is besides an IMFTransform), as Windows' answers it
// (MFColorConverterDmoProbe, ColorConverterDmo.windows.txt), over the transform itself (its vtable, the same object):
//
//   - one stream each way (DMO_E_INVALIDSTREAMINDEX of another); its 20 input types of the major type and subtype
//     (DirectShow's RGB subtypes), fixed-size samples, no format; once there is an input, its 16 outputs of a
//     VIDEOINFOHEADER of the input's size and frame time, their bit count, compression, image size (565's bitfields
//     and 555's masks, RGB8's halftone palette after it);
//   - a type set of a VIDEOINFOHEADER (or VIDEOINFOHEADER2, or MFVIDEOFORMAT) is the transform's of the same: its size,
//     rate (of the frame time), stride (an RGB of a positive height bottom-up), sample size; read back as set; a type
//     the transform was given read back as MFVIDEOFORMAT (MFCreateAMMediaTypeFromMFMediaType's);
//   - a buffer in (DMO_E_NOTACCEPTING while one is held), its picture out into the application's buffer, of the
//     input's time, length and sync point; S_FALSE where there is none.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal sealed unsafe partial class MFColorConverter
    {
        public static readonly Guid IID_IMediaObject = new("d8ad0f58-5494-4102-97c5-ec798e59bcf4");
        private const int DMO_E_INVALIDSTREAMINDEX = unchecked((int)0x80040201), DMO_E_TYPE_NOT_SET = unchecked((int)0x80040203);
        private const int DMO_E_NOTACCEPTING = unchecked((int)0x80040204), DMO_E_TYPE_NOT_ACCEPTED = unchecked((int)0x80040205), DMO_E_NO_MORE_ITEMS = unchecked((int)0x80040206);
        private static readonly Guid FORMAT_VideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a"), FORMAT_VideoInfo2 = new("f72a76a0-eb0a-11d0-ace4-0000c0cc16ba");
        private static readonly Guid FORMAT_MFVideoFormat = new("aed4ab2d-7326-43cb-9464-c879cab9c43d");

        // DirectShow's RGB subtypes of Media Foundation's
        private static readonly (Guid MF, Guid DShow)[] s_rgb =
        {
            (MFVideoProcessor.RGB32, new("e436eb7e-524f-11ce-9f53-0020af0ba770")), (MFVideoProcessor.RGB24, new("e436eb7d-524f-11ce-9f53-0020af0ba770")),
            (MFVideoProcessor.RGB565, new("e436eb7b-524f-11ce-9f53-0020af0ba770")), (MFVideoProcessor.RGB555, new("e436eb7c-524f-11ce-9f53-0020af0ba770")),
            (MFVideoProcessor.ARGB32, new("773c9ac0-3274-11d0-b724-00aa006c1a01")), (MFVideoProcessor.P8, new("e436eb7a-524f-11ce-9f53-0020af0ba770")),   // (RGB8 of the other half: a literal, as initializers of two files run in no order)
        };
        private static Guid ToDShow(Guid s) { foreach (var (m, d) in s_rgb) if (m == s) return d; return s; }
        private static Guid ToMF(Guid s) { foreach (var (m, d) in s_rgb) if (d == s && d != RGB8) return m; return s; }
        private static bool IsRgb(Guid mf) => mf == MFVideoProcessor.RGB32 || mf == MFVideoProcessor.RGB24 || mf == MFVideoProcessor.RGB565 || mf == MFVideoProcessor.RGB555 || mf == MFVideoProcessor.ARGB32 || mf == RGB8 || mf == MFVideoProcessor.P8;

        /// <summary>The bit count of each output subtype (VIDEOINFOHEADER's biBitCount).</summary>
        private static ushort Bits(Guid mf) =>
            mf == MFVideoProcessor.RGB32 || mf == MFVideoProcessor.ARGB32 || mf == F("AYUV") || mf == V216 || mf == V410 ? (ushort)32
            : mf == MFVideoProcessor.RGB24 ? (ushort)24 : mf == F("YUY2") || mf == F("UYVY") || mf == F("YVYU") || mf == MFVideoProcessor.RGB565 || mf == MFVideoProcessor.RGB555 ? (ushort)16
            : mf == RGB8 || mf == MFVideoProcessor.P8 ? (ushort)8 : (ushort)12;

        /// <summary>A DMO_MEDIA_TYPE, managed: its GUIDs, flags, sample size, format type and block.</summary>
        private sealed class DmoType
        {
            public Guid Major, Sub, Format;
            public uint Fixed, Temporal, SampleSize;
            public byte[] Block = Array.Empty<byte>();
        }

        private DmoType? _dmoIn, _dmoOut;            // the types the DMO was given, while the transform's are those
        private IntPtr _dmoInOf, _dmoOutOf;           // (the transform's type objects they made)
        private uint _inFlags;                        // the held input's DMO_INPUT_DATA_BUFFER flags

        private static int PUnk => IntPtr.Size == 8 ? 64 : 60;
        private static int CbFormat => IntPtr.Size == 8 ? 72 : 64;
        private static int PbFormat => IntPtr.Size == 8 ? 80 : 68;

        private static DmoType ReadDmo(byte* mt)
        {
            var t = new DmoType { Major = *(Guid*)mt, Sub = *(Guid*)(mt + 16), Fixed = *(uint*)(mt + 32), Temporal = *(uint*)(mt + 36), SampleSize = *(uint*)(mt + 40), Format = *(Guid*)(mt + 44) };
            uint cb = *(uint*)(mt + CbFormat); byte* pb = *(byte**)(mt + PbFormat);
            if (cb > 0 && pb != null) t.Block = new ReadOnlySpan<byte>(pb, (int)cb).ToArray();
            return t;
        }

        /// <summary>The type into the caller's DMO_MEDIA_TYPE: its block in memory of CoTaskMemAlloc's (the caller's to free: MoFreeMediaType).</summary>
        private static void WriteDmo(DmoType t, byte* mt)
        {
            int size = IntPtr.Size == 8 ? 88 : 72;
            new Span<byte>(mt, size).Clear();
            *(Guid*)mt = t.Major; *(Guid*)(mt + 16) = t.Sub;
            *(uint*)(mt + 32) = t.Fixed; *(uint*)(mt + 36) = t.Temporal; *(uint*)(mt + 40) = t.SampleSize;
            *(Guid*)(mt + 44) = t.Format;
            if (t.Block.Length > 0)
            {
                var p = (byte*)Marshal.AllocCoTaskMem(t.Block.Length);
                t.Block.CopyTo(new Span<byte>(p, t.Block.Length));
                *(uint*)(mt + CbFormat) = (uint)t.Block.Length; *(byte**)(mt + PbFormat) = p;
            }
        }

        /// <summary>The transform's type of a DMO type: of its VIDEOINFOHEADER(2) or MFVIDEOFORMAT; null of one of no format it knows.</summary>
        private static MFMediaType? ToMFType(DmoType d)
        {
            var sub = ToMF(d.Sub);
            int w, h; long frameTime = 0;
            fixed (byte* b = d.Block)
            {
                if (d.Format == FORMAT_VideoInfo && d.Block.Length >= 88) { w = *(int*)(b + 52); h = *(int*)(b + 56); frameTime = *(long*)(b + 40); }
                else if (d.Format == FORMAT_VideoInfo2 && d.Block.Length >= 112) { w = *(int*)(b + 76); h = *(int*)(b + 80); frameTime = *(long*)(b + 40); }
                else if (d.Format == FORMAT_MFVideoFormat && d.Block.Length >= 176)
                {
                    w = *(int*)(b + 8); h = *(int*)(b + 12);
                    uint n = *(uint*)(b + 48), dd = *(uint*)(b + 52);
                    if (n > 0 && dd > 0) frameTime = 10_000_000L * dd / n;
                }
                else return null;
            }
            var t = new MFMediaType();
            var a = t.Attributes;
            a.Set(MF.MF_MT_MAJOR_TYPE, d.Major);
            a.Set(MF.MF_MT_SUBTYPE, sub);
            a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)Math.Abs(h)));
            if (frameTime > 0) { uint n, dd; MFExports.MFAverageTimePerFrameToFrameRate((ulong)frameTime, &n, &dd); a.Set(MF.MF_MT_FRAME_RATE, MF.Pack(n, dd)); }
            a.Set(MF_MT_INTERLACE_MODE, 2u);
            a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, d.Temporal == 0 ? 1u : 0u);
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, d.Fixed != 0 ? 1u : 0u);
            if (d.SampleSize != 0) a.Set(MF_MT_SAMPLE_SIZE, d.SampleSize);
            if (sub != V216 && sub != V410)
            {
                int stride = Pictures.Stride(sub, w);
                // an RGB of a positive height is bottom-up (DirectShow's)
                if (IsRgb(sub) && h > 0) stride = -stride;
                a.Set(MF_MT_DEFAULT_STRIDE, unchecked((uint)stride));
            }
            return t;
        }

        /// <summary>MFCreateAMMediaTypeFromMFMediaType's of a type the transform was given: an MFVIDEOFORMAT block.</summary>
        private static DmoType FromMFType(MFMediaType t)
        {
            var a = t.Attributes;
            var d = new DmoType
            {
                Major = a.Guid(MF.MF_MT_MAJOR_TYPE), Sub = ToDShow(t.Subtype), Format = FORMAT_MFVideoFormat,
                Fixed = a.U32(MF.MF_MT_FIXED_SIZE_SAMPLES), Temporal = a.U32(MF.MF_MT_ALL_SAMPLES_INDEPENDENT) != 0 ? 0u : 1u, SampleSize = a.U32(MF_MT_SAMPLE_SIZE),
                Block = new byte[176],
            };
            var (w, h) = t.FrameSize;
            var (pn, pd) = MF.Unpack(a.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
            var (fn, fd) = MF.Unpack(a.U64(MF.MF_MT_FRAME_RATE));
            fixed (byte* b = d.Block)
            {
                *(uint*)b = 176;
                *(uint*)(b + 8) = w; *(uint*)(b + 12) = h; *(uint*)(b + 16) = pn; *(uint*)(b + 20) = pd;
                *(uint*)(b + 28) = a.U32(MF_MT_INTERLACE_MODE);
                *(uint*)(b + 48) = fn; *(uint*)(b + 52) = fd;
                *(Guid*)(b + 120) = t.Subtype;
                *(uint*)(b + 160) = (uint)t.Subtype.ToByteArray()[0] | (uint)t.Subtype.ToByteArray()[1] << 8 | (uint)t.Subtype.ToByteArray()[2] << 16 | (uint)t.Subtype.ToByteArray()[3] << 24;
            }
            return d;
        }

        /// <summary>An output type of the input: a VIDEOINFOHEADER of its size and frame time; the subtype's bit count, compression, size; 565's and 555's masks, RGB8's palette.</summary>
        private DmoType OutputDmo(Guid mf, MFMediaType input)
        {
            var (w, h) = input.FrameSize;
            var (rn, rd) = MF.Unpack(input.Attributes.U64(MF.MF_MT_FRAME_RATE));
            long frameTime = rn > 0 ? 10_000_000L * rd / rn : 0;
            uint size = (uint)Size(mf, (int)w, (int)h);
            int extra = mf == MFVideoProcessor.RGB565 || mf == MFVideoProcessor.RGB555 ? 12 : mf == RGB8 ? 1024 : 0;
            var block = new byte[88 + extra];
            fixed (byte* b = block)
            {
                *(long*)(b + 40) = frameTime;
                byte* bih = b + 48;
                *(uint*)bih = 40; *(int*)(bih + 4) = (int)w; *(int*)(bih + 8) = (int)h; *(ushort*)(bih + 12) = 1; *(ushort*)(bih + 14) = Bits(mf);
                bool fourcc = !IsRgb(mf);
                *(uint*)(bih + 16) = fourcc ? (uint)mf.ToByteArray()[0] | (uint)mf.ToByteArray()[1] << 8 | (uint)mf.ToByteArray()[2] << 16 | (uint)mf.ToByteArray()[3] << 24 : mf == MFVideoProcessor.RGB565 ? 3u : 0u;
                *(uint*)(bih + 20) = size;
                if (mf == MFVideoProcessor.RGB565) { *(uint*)(b + 88) = 0xF800; *(uint*)(b + 92) = 0x07E0; *(uint*)(b + 96) = 0x001F; }
                if (mf == MFVideoProcessor.RGB555) { *(uint*)(b + 88) = 0x7C00; *(uint*)(b + 92) = 0x03E0; *(uint*)(b + 96) = 0x001F; }
            }
            if (mf == RGB8) s_palette.AsSpan(0, 1024).CopyTo(block.AsSpan(88));
            return new DmoType { Major = MF.MFMediaType_Video, Sub = ToDShow(mf), Fixed = 1, SampleSize = size, Format = FORMAT_VideoInfo, Block = block };
        }

        private static Vtable BuildDmo()
        {
            var vt = new Vtable("IMediaObject", 24, new Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4"));     // (a literal: s_dmo is made by the other half's initializers)
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetStreamCount);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetStreamInfo);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetStreamInfo);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, uint, byte*, int>)&GetInputType);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint, byte*, int>)&GetOutputType);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, byte*, uint, int>)&SetInputTypeDmo);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, byte*, uint, int>)&SetOutputTypeDmo);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetInputCurrentType);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetOutputCurrentType);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, uint*, uint*, uint*, int>)&GetInputSizeInfo);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, uint*, uint*, int>)&GetOutputSizeInfo);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, long*, int>)&GetInputMaxLatency);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, long, int>)&SetInputMaxLatency);
            vt.Set(16, (delegate* unmanaged<IntPtr, int>)&FlushDmo);
            vt.Set(17, (delegate* unmanaged<IntPtr, uint, int>)&Discontinuity);
            vt.Set(18, (delegate* unmanaged<IntPtr, int>)&Resources);
            vt.Set(19, (delegate* unmanaged<IntPtr, int>)&Resources);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStatus);
            vt.Set(21, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, long, long, int>)&ProcessInputDmo);
            vt.Set(22, (delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)&ProcessOutputDmo);
            vt.Set(23, (delegate* unmanaged<IntPtr, int, int>)&LockDmo);
            return vt;
        }

        private static MFColorConverter? C(IntPtr self) => From<MFColorConverter>(self);

        [UnmanagedCallersOnly] private static int GetStreamCount(IntPtr self, uint* i, uint* o) { if (i == null || o == null) return MF.E_POINTER; *i = 1; *o = 1; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetStreamInfo(IntPtr self, uint index, uint* flags) { if (index != 0) return DMO_E_INVALIDSTREAMINDEX; if (flags == null) return MF.E_POINTER; *flags = 0; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetInputType(IntPtr self, uint index, uint n, byte* mt)
        {
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            if (n >= Inputs.Length) return DMO_E_NO_MORE_ITEMS;
            if (mt != null) WriteDmo(new DmoType { Major = MF.MFMediaType_Video, Sub = ToDShow(Inputs[n]), Fixed = 1 }, mt);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputType(IntPtr self, uint index, uint n, byte* mt)
        {
            if (C(self) is not { } c) return MF.E_POINTER;
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            if (n >= OutputCount) return DMO_E_NO_MORE_ITEMS;
            if (mt == null) return MF.S_OK;
            lock (c.Lock)
            {
                if (c.In == IntPtr.Zero) { WriteDmo(new DmoType { Major = MF.MFMediaType_Video, Sub = ToDShow(Inputs[n]), Fixed = 1 }, mt); return MF.S_OK; }
                var input = Read(c.In);
                try { WriteDmo(c.OutputDmo(Inputs[n], input), mt); }
                finally { input.Release(); }
            }
            return MF.S_OK;
        }

        private int SetDmo(bool input, byte* mt, uint flags)
        {
            bool test = (flags & 1) != 0, clear = (flags & 2) != 0;
            int slot = input ? 15 : 16;
            if (clear)
            {
                int ch = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(Pointer, slot))(Pointer, 0, IntPtr.Zero, 0);
                if (input) _dmoIn = null; else _dmoOut = null;
                return ch;
            }
            if (mt == null) return MF.E_POINTER;
            var d = ReadDmo(mt);
            var t = ToMFType(d);
            if (t == null) return DMO_E_TYPE_NOT_ACCEPTED;
            try
            {
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(Pointer, slot))(Pointer, 0, t.Pointer, test ? 1u : 0u);
                if (hr < 0) return DMO_E_TYPE_NOT_ACCEPTED;
                if (!test)
                {
                    lock (Lock)
                    {
                        if (input) { _dmoIn = d; _dmoInOf = In; } else { _dmoOut = d; _dmoOutOf = Out; }
                    }
                }
                return MF.S_OK;
            }
            finally { t.Release(); }
        }

        [UnmanagedCallersOnly] private static int SetInputTypeDmo(IntPtr self, uint index, byte* mt, uint flags) => index != 0 ? DMO_E_INVALIDSTREAMINDEX : C(self)?.SetDmo(true, mt, flags) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int SetOutputTypeDmo(IntPtr self, uint index, byte* mt, uint flags) => index != 0 ? DMO_E_INVALIDSTREAMINDEX : C(self)?.SetDmo(false, mt, flags) ?? MF.E_POINTER;

        private int CurrentDmo(bool input, byte* mt)
        {
            if (mt == null) return MF.E_POINTER;
            lock (Lock)
            {
                IntPtr of = input ? In : Out;
                if (of == IntPtr.Zero) return DMO_E_TYPE_NOT_SET;
                var given = input ? (_dmoInOf == of ? _dmoIn : null) : (_dmoOutOf == of ? _dmoOut : null);
                if (given != null) { WriteDmo(given, mt); return MF.S_OK; }
                var t = Read(of);
                try { WriteDmo(FromMFType(t), mt); }
                finally { t.Release(); }
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetInputCurrentType(IntPtr self, uint index, byte* mt) => index != 0 ? DMO_E_INVALIDSTREAMINDEX : C(self)?.CurrentDmo(true, mt) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int GetOutputCurrentType(IntPtr self, uint index, byte* mt) => index != 0 ? DMO_E_INVALIDSTREAMINDEX : C(self)?.CurrentDmo(false, mt) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int GetInputSizeInfo(IntPtr self, uint index, uint* size, uint* lookahead, uint* align)
        {
            if (C(self) is not { } c) return MF.E_POINTER;
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            lock (c.Lock)
            {
                if (c.In == IntPtr.Zero) return DMO_E_TYPE_NOT_SET;
                if (size != null) *size = c.InputSize();
                if (lookahead != null) *lookahead = 0;
                if (align != null) *align = 1;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputSizeInfo(IntPtr self, uint index, uint* size, uint* align)
        {
            if (C(self) is not { } c) return MF.E_POINTER;
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            lock (c.Lock)
            {
                if (c.Out == IntPtr.Zero) return DMO_E_TYPE_NOT_SET;
                if (size != null) *size = c.OutputSize();
                if (align != null) *align = 1;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetInputMaxLatency(IntPtr self, uint index, long* latency) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int SetInputMaxLatency(IntPtr self, uint index, long latency) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int FlushDmo(IntPtr self) { if (C(self) is not { } c) return MF.E_POINTER; ((delegate* unmanaged<IntPtr, uint, nuint, int>)Com.Fn(c.Pointer, 23))(c.Pointer, 0, 0); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int Discontinuity(IntPtr self, uint index) => index != 0 ? DMO_E_INVALIDSTREAMINDEX : MF.S_OK;
        [UnmanagedCallersOnly] private static int Resources(IntPtr self) => MF.S_OK;
        [UnmanagedCallersOnly] private static int LockDmo(IntPtr self, int locking) => MF.S_OK;

        /// <summary>DMO_INPUT_STATUSF_ACCEPT_DATA while no buffer is held.</summary>
        [UnmanagedCallersOnly]
        private static int GetInputStatus(IntPtr self, uint index, uint* flags)
        {
            if (C(self) is not { } c || flags == null) return MF.E_POINTER;
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            lock (c.Lock) *flags = c._held == IntPtr.Zero ? 1u : 0u;
            return MF.S_OK;
        }

        /// <summary>The buffer's bytes as a sample of the transform's: of the time (DMO_INPUT_DATA_BUFFERF_TIME) and length (_TIMELENGTH) given.</summary>
        [UnmanagedCallersOnly]
        private static int ProcessInputDmo(IntPtr self, uint index, IntPtr buffer, uint flags, long time, long length)
        {
            if (C(self) is not { } c || buffer == IntPtr.Zero) return MF.E_POINTER;
            if (index != 0) return DMO_E_INVALIDSTREAMINDEX;
            lock (c.Lock) { if (c.In == IntPtr.Zero || c.Out == IntPtr.Zero) return DMO_E_TYPE_NOT_SET; if (c._held != IntPtr.Zero) return DMO_E_NOTACCEPTING; }
            byte* p; uint n;
            int hr = ((delegate* unmanaged<IntPtr, byte**, uint*, int>)Com.Fn(buffer, 5))(buffer, &p, &n);           // IMediaBuffer::GetBufferAndLength
            if (hr < 0) return hr;
            var sample = new MFSample();
            var buf = new MFMemoryBuffer(Math.Max(1, n));
            new ReadOnlySpan<byte>(p, (int)n).CopyTo(new Span<byte>(buf.LockBytes(true), (int)n));
            buf.CurrentLength = n;
            sample.SetBuffer(buf.Pointer);
            buf.Release();
            if ((flags & 2) != 0) sample.Time = time;
            if ((flags & 4) != 0) sample.Duration = length;
            if ((flags & 1) != 0) sample.Attributes.Set(MF.MFSampleExtension_CleanPoint, MFValue.Of(1u));
            try
            {
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(c.Pointer, 24))(c.Pointer, 0, sample.Pointer, 0);
                if (hr == MF.MF_E_NOTACCEPTING) return DMO_E_NOTACCEPTING;
                if (hr >= 0) lock (c.Lock) c._inFlags = flags;
                return hr;
            }
            finally { sample.Release(); }
        }

        /// <summary>The picture into the application's buffer: DMO_OUTPUT_DATA_BUFFER's status of the input's sync point, time and length; S_FALSE of none.</summary>
        [UnmanagedCallersOnly]
        private static int ProcessOutputDmo(IntPtr self, uint flags, uint count, byte* buffers, uint* status)
        {
            if (C(self) is not { } c || buffers == null || status == null) return MF.E_POINTER;
            if (count != 1) return MF.E_INVALIDARG;
            *status = 0;
            IntPtr target = *(IntPtr*)buffers;
            int at = IntPtr.Size, ts = IntPtr.Size == 8 ? 16 : 8;
            *(uint*)(buffers + at) = 0;
            uint inFlags;
            lock (c.Lock) { if (c.In == IntPtr.Zero || c.Out == IntPtr.Zero) return DMO_E_TYPE_NOT_SET; if (c._held == IntPtr.Zero) return 1; inFlags = c._inFlags; }
            uint size = c.OutputSize();
            var sample = new MFSample();
            var buf = new MFMemoryBuffer(Math.Max(1, size));
            sample.SetBuffer(buf.Pointer);
            buf.Release();
            try
            {
                int odbSize = 4 * IntPtr.Size;
                var odb = stackalloc byte[odbSize];
                new Span<byte>(odb, odbSize).Clear();
                *(IntPtr*)(odb + IntPtr.Size) = sample.Pointer;
                uint st;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Com.Fn(c.Pointer, 25))(c.Pointer, 0, 1, odb, &st);
                if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return 1;
                if (hr < 0) return hr;
                var bytes = sample.Bytes();
                if (target != IntPtr.Zero)
                {
                    byte* p; uint n, max;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(target, 4))(target, &max);                           // GetMaxLength
                    ((delegate* unmanaged<IntPtr, byte**, uint*, int>)Com.Fn(target, 5))(target, &p, &n);                  // GetBufferAndLength
                    int len = (int)Math.Min(max, (uint)bytes.Length);
                    bytes.AsSpan(0, len).CopyTo(new Span<byte>(p, len));
                    ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(target, 3))(target, (uint)len);                         // SetLength
                }
                uint outFlags = inFlags & 1;
                if (sample.Time is { } t) { outFlags |= 2; *(long*)(buffers + ts) = t; }
                if (sample.Duration is { } d) { outFlags |= 4; *(long*)(buffers + ts + 8) = d; }
                *(uint*)(buffers + at) = outFlags;
                return MF.S_OK;
            }
            finally { sample.Release(); }
        }
    }
}
