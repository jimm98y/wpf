// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An effect MFT of the application's, for the tests of the source reader's transforms: it passes its samples
// through, takes and gives the subtypes it is made for, and logs every call the reader makes of it -- what
// Windows' reader asks of a transform added with AddTransformForStream, and in what order.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal sealed unsafe class SpyEffect : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private const int E_NOTIMPL = unchecked((int)0x80004001), TYPE_NOT_SET = unchecked((int)0xC00D6D60), NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        private const int INVALIDMEDIATYPE = unchecked((int)0xC00D36B4), NEED_MORE_INPUT = unchecked((int)0xC00D6D72), NOTACCEPTING = unchecked((int)0xC00D36B5);

        public readonly List<string> Calls = new();
        public readonly Guid Major;
        /// <summary>The subtypes it takes in (and gives out the same), and offers as its input types, in order.</summary>
        public readonly Guid[] Accepts, Offers;
        public readonly bool Provides;
        private IntPtr _in, _out, _held;
        public int Inputs;

        public SpyEffect(Guid major, Guid[] accepts, Guid[] offers, bool provides) : base(s_vt) { Major = major; Accepts = accepts; Offers = offers; Provides = provides; }

        protected override void OnFinalRelease() { MFReader.Release(_in); MFReader.Release(_out); MFReader.Release(_held); }

        private static SpyEffect S(IntPtr self) => From<SpyEffect>(self)!;

        private static string T(IntPtr t)
        {
            if (t == IntPtr.Zero) return "null";
            var (w, h) = MF.Unpack(MFReader.U64(t, FRAME_SIZE));
            return MFReader.Name(MFReader.G(t, SUB)) + (w > 0 ? $" {w}x{h}" : "");
        }

        private static IntPtr Copy(IntPtr t)
        {
            var c = MFReader.MediaType();
            ((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(t, 32))(t, c);                      // IMFAttributes::CopyAllItems
            return c;
        }

        private static Vtable Build()
        {
            var vt = new Vtable("IMFTransform", 26, new Guid("bf94c121-5b05-4e6f-8000-ba598961414d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)&GetStreamLimits);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetStreamCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)&GetStreamIDs);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetInputStreamInfo);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetOutputStreamInfo);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetAttributes);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputStreamAttributes);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputStreamAttributes);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, int>)&DeleteInputStream);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, uint*, int>)&AddInputStreams);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetInputAvailableType);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetOutputAvailableType);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetInputType);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetOutputType);
            vt.Set(17, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputCurrentType);
            vt.Set(18, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputCurrentType);
            vt.Set(19, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStatus);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint*, int>)&GetOutputStatus);
            vt.Set(21, (delegate* unmanaged<IntPtr, long, long, int>)&SetOutputBounds);
            vt.Set(22, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&ProcessEvent);
            vt.Set(23, (delegate* unmanaged<IntPtr, uint, nuint, int>)&ProcessMessage);
            vt.Set(24, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&ProcessInput);
            vt.Set(25, (delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)&ProcessOutput);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetStreamLimits(IntPtr self, uint* a, uint* b, uint* c, uint* d) { S(self).Calls.Add("GetStreamLimits"); *a = *b = *c = *d = 1; return 0; }
        [UnmanagedCallersOnly] private static int GetStreamCount(IntPtr self, uint* i, uint* o) { S(self).Calls.Add("GetStreamCount"); *i = *o = 1; return 0; }
        [UnmanagedCallersOnly] private static int GetStreamIDs(IntPtr self, uint ni, uint* i, uint no, uint* o) { S(self).Calls.Add("GetStreamIDs"); return E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int GetInputStreamInfo(IntPtr self, uint id, byte* info) { S(self).Calls.Add("GetInputStreamInfo"); new Span<byte>(info, 24).Clear(); return 0; }

        [UnmanagedCallersOnly]
        private static int GetOutputStreamInfo(IntPtr self, uint id, uint* info)
        {
            var s = S(self);
            s.Calls.Add("GetOutputStreamInfo");
            info[0] = 0x7 | (s.Provides ? 0x100u : 0);
            info[1] = 1 << 20;
            info[2] = 0;
            return 0;
        }

        [UnmanagedCallersOnly] private static int GetAttributes(IntPtr self, IntPtr* a) { S(self).Calls.Add("GetAttributes"); *a = IntPtr.Zero; return E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int GetInputStreamAttributes(IntPtr self, uint id, IntPtr* a) { S(self).Calls.Add("GetInputStreamAttributes"); *a = IntPtr.Zero; return E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int GetOutputStreamAttributes(IntPtr self, uint id, IntPtr* a) { S(self).Calls.Add("GetOutputStreamAttributes"); *a = IntPtr.Zero; return E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int DeleteInputStream(IntPtr self, uint id) => E_NOTIMPL;
        [UnmanagedCallersOnly] private static int AddInputStreams(IntPtr self, uint n, uint* ids) => E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetInputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            var s = S(self);
            s.Calls.Add($"GetInputAvailableType {i}");
            *type = IntPtr.Zero;
            if (i >= s.Offers.Length) return NO_MORE_TYPES;
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, s.Major); MFReader.SetGuid(t, SUB, s.Offers[i]);
            *type = t;
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            var s = S(self);
            s.Calls.Add($"GetOutputAvailableType {i}");
            *type = IntPtr.Zero;
            if (s._in == IntPtr.Zero) return TYPE_NOT_SET;
            if (i > 0) return NO_MORE_TYPES;
            *type = Copy(s._in);
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int SetInputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            var s = S(self);
            bool ok = type == IntPtr.Zero || Array.IndexOf(s.Accepts, MFReader.G(type, SUB)) >= 0;
            s.Calls.Add($"SetInputType {T(type)}{((flags & 1) != 0 ? " test" : "")}: {(ok ? "ok" : "refused")}");
            if (!ok) return INVALIDMEDIATYPE;
            if ((flags & 1) != 0) return 0;
            MFReader.Release(s._in);
            s._in = type == IntPtr.Zero ? IntPtr.Zero : Copy(type);
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int SetOutputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            var s = S(self);
            bool ok = type == IntPtr.Zero || s._in != IntPtr.Zero && MFReader.G(type, SUB) == MFReader.G(s._in, SUB);
            s.Calls.Add($"SetOutputType {T(type)}{((flags & 1) != 0 ? " test" : "")}: {(ok ? "ok" : "refused")}");
            if (!ok) return s._in == IntPtr.Zero ? TYPE_NOT_SET : INVALIDMEDIATYPE;
            if ((flags & 1) != 0) return 0;
            MFReader.Release(s._out);
            s._out = type == IntPtr.Zero ? IntPtr.Zero : Copy(type);
            return 0;
        }

        [UnmanagedCallersOnly] private static int GetInputCurrentType(IntPtr self, uint id, IntPtr* t) { var s = S(self); s.Calls.Add("GetInputCurrentType"); *t = s._in == IntPtr.Zero ? IntPtr.Zero : Copy(s._in); return s._in == IntPtr.Zero ? TYPE_NOT_SET : 0; }
        [UnmanagedCallersOnly] private static int GetOutputCurrentType(IntPtr self, uint id, IntPtr* t) { var s = S(self); s.Calls.Add("GetOutputCurrentType"); *t = s._out == IntPtr.Zero ? IntPtr.Zero : Copy(s._out); return s._out == IntPtr.Zero ? TYPE_NOT_SET : 0; }
        [UnmanagedCallersOnly] private static int GetInputStatus(IntPtr self, uint id, uint* f) { var s = S(self); *f = s._held == IntPtr.Zero ? 1u : 0u; return 0; }
        [UnmanagedCallersOnly] private static int GetOutputStatus(IntPtr self, uint* f) => E_NOTIMPL;
        [UnmanagedCallersOnly] private static int SetOutputBounds(IntPtr self, long a, long b) => E_NOTIMPL;
        [UnmanagedCallersOnly] private static int ProcessEvent(IntPtr self, uint id, IntPtr e) => E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int ProcessMessage(IntPtr self, uint m, nuint p)
        {
            var s = S(self);
            string name = m switch { 0 => "FLUSH", 1 => "DRAIN", 2 => "SET_D3D_MANAGER", 0x10000000 => "BEGIN_STREAMING", 0x10000001 => "END_STREAMING", 0x10000002 => "END_OF_STREAM", 0x10000003 => "START_OF_STREAM", _ => $"0x{m:x}" };
            if (s.Calls.Count == 0 || s.Calls[^1] != "ProcessMessage " + name) s.Calls.Add("ProcessMessage " + name);
            if (m == 0) { MFReader.Release(s._held); s._held = IntPtr.Zero; }
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int ProcessInput(IntPtr self, uint id, IntPtr sample, uint flags)
        {
            var s = S(self);
            if (s._held != IntPtr.Zero) return NOTACCEPTING;
            ((delegate* unmanaged<IntPtr, uint>)MFReader.Fn(sample, 1))(sample);
            s._held = sample;
            s.Inputs++;
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int ProcessOutput(IntPtr self, uint flags, uint count, byte* buffers, uint* status)
        {
            var s = S(self);
            *status = 0;
            if (s._held == IntPtr.Zero) return NEED_MORE_INPUT;
            if (s.Provides || *(IntPtr*)(buffers + IntPtr.Size) == IntPtr.Zero)
            {
                *(IntPtr*)(buffers + IntPtr.Size) = s._held;                 // the input itself, passed through
                s._held = IntPtr.Zero;
                return 0;
            }
            // into the reader's sample: the bytes, times and attributes of the input
            IntPtr dst = *(IntPtr*)(buffers + IntPtr.Size);
            var bytes = MFReader.SampleBytes(s._held, out _);
            IntPtr buf;
            ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(dst, 40))(dst, 0, &buf);
            byte* p; uint max, cur;
            ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)MFReader.Fn(buf, 3))(buf, &p, &max, &cur);
            bytes.AsSpan(0, Math.Min(bytes.Length, (int)max)).CopyTo(new Span<byte>(p, (int)max));
            ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(buf, 4))(buf);
            ((delegate* unmanaged<IntPtr, uint, int>)MFReader.Fn(buf, 6))(buf, (uint)Math.Min(bytes.Length, (int)max));
            MFReader.Release(buf);
            long t; if (((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s._held, 35))(s._held, &t) >= 0) ((delegate* unmanaged<IntPtr, long, int>)MFReader.Fn(dst, 36))(dst, t);
            long d; if (((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s._held, 37))(s._held, &d) >= 0) ((delegate* unmanaged<IntPtr, long, int>)MFReader.Fn(dst, 38))(dst, d);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(s._held, 32))(s._held, dst);
            MFReader.Release(s._held);
            s._held = IntPtr.Zero;
            return 0;
        }
    }
}
