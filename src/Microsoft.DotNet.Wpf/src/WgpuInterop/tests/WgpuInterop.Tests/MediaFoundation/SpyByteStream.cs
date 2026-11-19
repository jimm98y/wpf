// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A byte stream of the application's, in memory, for the tests of what writes to one (the sink writer): an
// IMFByteStream that keeps its bytes and logs every call made of it.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal sealed unsafe class SpyByteStream : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly List<string> Calls = new();
        public byte[] Data = new byte[0];
        public long Length, Position;
        public uint Capabilities;

        /// <summary>A stream of the capabilities given (MFBYTESTREAM_IS_READABLE 1, WRITABLE 2, SEEKABLE 4).</summary>
        public SpyByteStream(uint capabilities = 7) : base(s_vt) { Capabilities = capabilities; }

        public byte[] Bytes() => Data.AsSpan(0, (int)Length).ToArray();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFByteStream", 18, new Guid("ad4c1b00-4bf7-422f-9175-756693d9130d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCapabilities);
            vt.Set(4, (delegate* unmanaged<IntPtr, ulong*, int>)&GetLength);
            vt.Set(5, (delegate* unmanaged<IntPtr, ulong, int>)&SetLength);
            vt.Set(6, (delegate* unmanaged<IntPtr, ulong*, int>)&GetCurrentPosition);
            vt.Set(7, (delegate* unmanaged<IntPtr, ulong, int>)&SetCurrentPosition);
            vt.Set(8, (delegate* unmanaged<IntPtr, int*, int>)&IsEndOfStream);
            vt.Set(9, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Read);
            vt.Set(10, (delegate* unmanaged<IntPtr, byte*, uint, IntPtr, IntPtr, int>)&BeginRead);
            vt.Set(11, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&EndRead);
            vt.Set(12, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Write);
            vt.Set(13, (delegate* unmanaged<IntPtr, byte*, uint, IntPtr, IntPtr, int>)&BeginWrite);
            vt.Set(14, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&EndWrite);
            vt.Set(15, (delegate* unmanaged<IntPtr, int, long, uint, ulong*, int>)&Seek);
            vt.Set(16, (delegate* unmanaged<IntPtr, int>)&Flush);
            vt.Set(17, (delegate* unmanaged<IntPtr, int>)&Close);
            return vt;
        }

        private static SpyByteStream S(IntPtr self) => From<SpyByteStream>(self)!;

        private void Log(string c) { lock (Calls) { if (Calls.Count > 0 && Calls[^1].StartsWith(c.Split(' ')[0]) && c.StartsWith("Write ") && Calls[^1].StartsWith("Write ")) { Calls[^1] = Merge(Calls[^1], c); return; } Calls.Add(c); } }

        /// <summary>Runs of writes, each after the last, as one: their count and bytes.</summary>
        private static string Merge(string a, string b)
        {
            // "Write n at p (k writes)" form
            var pa = a.Split(' '); var pb = b.Split(' ');
            long na = long.Parse(pa[1]), at = long.Parse(pa[3]), nb = long.Parse(pb[1]), bt = long.Parse(pb[3]);
            int k = a.Contains('(') ? int.Parse(a[(a.IndexOf('(') + 1)..].Split(' ')[0]) : 1;
            if (bt != at + na) return a + "|" + b;
            return $"Write {na + nb} at {at} ({k + 1} writes)";
        }

        private void Grow(long n) { if (n > Data.Length) Array.Resize(ref Data, (int)Math.Max(n, Data.Length * 2L)); }

        [UnmanagedCallersOnly] private static int GetCapabilities(IntPtr self, uint* c) { var s = S(self); s.Log("GetCapabilities"); *c = s.Capabilities; return 0; }
        [UnmanagedCallersOnly] private static int GetLength(IntPtr self, ulong* l) { var s = S(self); s.Log("GetLength"); *l = (ulong)s.Length; return 0; }
        [UnmanagedCallersOnly] private static int SetLength(IntPtr self, ulong l) { var s = S(self); s.Log($"SetLength {l}"); s.Grow((long)l); s.Length = (long)l; return 0; }
        [UnmanagedCallersOnly] private static int GetCurrentPosition(IntPtr self, ulong* p) { var s = S(self); s.Log("GetCurrentPosition"); *p = (ulong)s.Position; return 0; }
        [UnmanagedCallersOnly] private static int SetCurrentPosition(IntPtr self, ulong p) { var s = S(self); s.Log($"SetCurrentPosition {p}"); s.Position = (long)p; return 0; }
        [UnmanagedCallersOnly] private static int IsEndOfStream(IntPtr self, int* e) { var s = S(self); s.Log("IsEndOfStream"); *e = s.Position >= s.Length ? 1 : 0; return 0; }

        [UnmanagedCallersOnly]
        private static int Read(IntPtr self, byte* p, uint n, uint* got)
        {
            var s = S(self);
            s.Log($"Read {n} at {s.Position}");
            int k = (int)Math.Max(0, Math.Min(n, s.Length - s.Position));
            if (k > 0) s.Data.AsSpan((int)s.Position, k).CopyTo(new Span<byte>(p, k));
            s.Position += k;
            *got = (uint)k;
            return 0;
        }

        [UnmanagedCallersOnly] private static int BeginRead(IntPtr self, byte* p, uint n, IntPtr cb, IntPtr st) { S(self).Log("BeginRead"); return unchecked((int)0x80004001); }
        [UnmanagedCallersOnly] private static int EndRead(IntPtr self, IntPtr r, uint* n) { S(self).Log("EndRead"); return unchecked((int)0x80004001); }

        [UnmanagedCallersOnly]
        private static int Write(IntPtr self, byte* p, uint n, uint* wrote)
        {
            var s = S(self);
            s.Log($"Write {n} at {s.Position}");
            s.Grow(s.Position + n);
            new ReadOnlySpan<byte>(p, (int)n).CopyTo(s.Data.AsSpan((int)s.Position));
            s.Position += n;
            s.Length = Math.Max(s.Length, s.Position);
            *wrote = n;
            return 0;
        }

        /// <summary>BeginWrite: written at once, the callback invoked (through MFInvokeCallback's like: an async result of its own).</summary>
        [UnmanagedCallersOnly]
        private static int BeginWrite(IntPtr self, byte* p, uint n, IntPtr cb, IntPtr st)
        {
            var s = S(self);
            s.Log($"BeginWrite {n} at {s.Position}");
            s.Grow(s.Position + n);
            new ReadOnlySpan<byte>(p, (int)n).CopyTo(s.Data.AsSpan((int)s.Position));
            s.Position += n;
            s.Length = Math.Max(s.Length, s.Position);
            s._lastWrite = n;
            var r = new AsyncResult(cb, st);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                ((delegate* unmanaged<IntPtr, IntPtr, int>)((IntPtr*)*(IntPtr*)cb)[4])(cb, r.Pointer);
                r.Release();
            });
            return 0;
        }

        private uint _lastWrite;

        [UnmanagedCallersOnly] private static int EndWrite(IntPtr self, IntPtr r, uint* n) { var s = S(self); s.Log("EndWrite"); *n = s._lastWrite; return 0; }

        [UnmanagedCallersOnly]
        private static int Seek(IntPtr self, int origin, long offset, uint flags, ulong* pos)
        {
            var s = S(self);
            s.Log($"Seek {(origin == 0 ? "begin" : "current")} {offset}");
            s.Position = origin == 0 ? offset : s.Position + offset;
            if (pos != null) *pos = (ulong)s.Position;
            return 0;
        }

        [UnmanagedCallersOnly] private static int Flush(IntPtr self) { S(self).Log("Flush"); return 0; }
        [UnmanagedCallersOnly] private static int Close(IntPtr self) { S(self).Log("Close"); return 0; }

    }

    // (at the namespace's level: the browser's runtime finds no callback of a nested class)
    /// <summary>IMFAsyncResult of a BeginWrite.</summary>
    internal sealed unsafe class AsyncResult : ComObject
    {
        private static readonly Vtable s_rv = BuildResult();
        private readonly IntPtr _cb, _state;
        public AsyncResult(IntPtr cb, IntPtr state) : base(s_rv) { _cb = cb; _state = state; ((delegate* unmanaged<IntPtr, uint>)((IntPtr*)*(IntPtr*)cb)[1])(cb); if (state != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)((IntPtr*)*(IntPtr*)state)[1])(state); }
        protected override void OnFinalRelease() { MFReader.Release(_cb); MFReader.Release(_state); }
        private static Vtable BuildResult()
        {
            var vt = new Vtable("IMFAsyncResult", 8, new Guid("ac6b7889-0740-4d51-8619-905994a55cc6"));
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetState);
            vt.Set(4, (delegate* unmanaged<IntPtr, int>)&GetStatus);
            vt.Set(5, (delegate* unmanaged<IntPtr, int, int>)&SetStatus);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetObject);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr>)&GetStateNoAddRef);
            return vt;
        }
        [UnmanagedCallersOnly] private static int GetState(IntPtr self, IntPtr* s) { var r = From<AsyncResult>(self)!; *s = r._state; if (r._state != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)((IntPtr*)*(IntPtr*)r._state)[1])(r._state); return 0; }
        [UnmanagedCallersOnly] private static int GetStatus(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static int SetStatus(IntPtr self, int hr) => 0;
        [UnmanagedCallersOnly] private static int GetObject(IntPtr self, IntPtr* o) { *o = IntPtr.Zero; return unchecked((int)0x80004003); }
        [UnmanagedCallersOnly] private static IntPtr GetStateNoAddRef(IntPtr self) => From<AsyncResult>(self)!._state;
    }
}
