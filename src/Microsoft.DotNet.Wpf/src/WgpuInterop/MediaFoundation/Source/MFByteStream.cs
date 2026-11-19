// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Byte streams (Documentation/media-foundation-shim.md, "The source reader"): IMFByteStream over a file
// (MFCreateFile), and the bytes of any IMFByteStream -- the application's own -- for the source reader.
//

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    /// <summary>IMFByteStream over a FileStream.</summary>
    internal sealed unsafe class MFFileByteStream : MFAttributesObject
    {
        public static readonly Guid IID_IMFByteStream = new("ad4c1b00-4bf7-422f-9175-756693d9130d");
        private static readonly Vtable s_vt = Build(), s_attributes = BuildAttributes();
        public readonly FileStream File;
        public readonly string Path;
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFByteStream", 18, IID_IMFByteStream);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCapabilities);
            vt.Set(4, (delegate* unmanaged<IntPtr, ulong*, int>)&GetLength);
            vt.Set(5, (delegate* unmanaged<IntPtr, ulong, int>)&SetLength);
            vt.Set(6, (delegate* unmanaged<IntPtr, ulong*, int>)&GetCurrentPosition);
            vt.Set(7, (delegate* unmanaged<IntPtr, ulong, int>)&SetCurrentPosition);
            vt.Set(8, (delegate* unmanaged<IntPtr, int*, int>)&IsEndOfStream);
            vt.Set(9, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Read);
            vt.Set(10, (delegate* unmanaged<IntPtr, byte*, uint, IntPtr, IntPtr, int>)&BeginRead);
            vt.Set(11, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&EndTransfer);
            vt.Set(12, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Write);
            vt.Set(13, (delegate* unmanaged<IntPtr, byte*, uint, IntPtr, IntPtr, int>)&BeginWrite);
            vt.Set(14, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&EndTransfer);
            vt.Set(15, (delegate* unmanaged<IntPtr, int, long, uint, ulong*, int>)&Seek);
            vt.Set(16, (delegate* unmanaged<IntPtr, int>)&Flush);
            vt.Set(17, (delegate* unmanaged<IntPtr, int>)&Close);
            return vt;
        }

        private static Vtable BuildAttributes()
        {
            var vt = new Vtable("IMFAttributes", 33, MF.IID_IMFAttributes);
            FillAttributes(vt);
            return vt;
        }

        public MFFileByteStream(FileStream file, string path) : base(s_vt, s_attributes)
        {
            File = file;
            Path = path;
            // what Windows' file byte stream says of itself
            Attributes.Set(SR.MF_BYTESTREAM_ORIGIN_NAME, MFValue.Of(path));
            Attributes.Set(SR.MF_BYTESTREAM_LAST_MODIFIED_TIME, MFValue.Of(BitConverter.GetBytes(System.IO.File.GetLastWriteTimeUtc(path).ToFileTimeUtc())));
        }

        protected override void OnFinalRelease()
        {
            File.Dispose();
            base.OnFinalRelease();
        }

        private static MFFileByteStream? S(IntPtr self) => From<MFFileByteStream>(self);

        // MFBYTESTREAM_IS_READABLE 1, IS_WRITABLE 2, IS_SEEKABLE 4, IS_REMOTE 8, IS_DIRECTORY 0x80, HAS_SLOW_SEEK 0x100
        [UnmanagedCallersOnly]
        private static int GetCapabilities(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            *c = (s.File.CanRead ? 1u : 0) | (s.File.CanWrite ? 2u : 0) | (s.File.CanSeek ? 4u : 0);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetLength(IntPtr self, ulong* l) { if (S(self) is not { } s || l == null) return MF.E_POINTER; lock (s._lock) *l = (ulong)s.File.Length; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetLength(IntPtr self, ulong l) { if (S(self) is not { } s) return MF.E_POINTER; try { lock (s._lock) s.File.SetLength((long)l); return MF.S_OK; } catch (Exception e) { return Hr(e); } }
        [UnmanagedCallersOnly] private static int GetCurrentPosition(IntPtr self, ulong* p) { if (S(self) is not { } s || p == null) return MF.E_POINTER; lock (s._lock) *p = (ulong)s.File.Position; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetCurrentPosition(IntPtr self, ulong p) { if (S(self) is not { } s) return MF.E_POINTER; if (p > long.MaxValue) return MF.E_INVALIDARG; lock (s._lock) s.File.Position = (long)p; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int IsEndOfStream(IntPtr self, int* e) { if (S(self) is not { } s || e == null) return MF.E_POINTER; lock (s._lock) *e = s.File.Position >= s.File.Length ? 1 : 0; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int Read(IntPtr self, byte* b, uint n, uint* read)
        {
            if (S(self) is not { } s || b == null) return MF.E_POINTER;
            try { uint r = s.ReadNow(b, n); if (read != null) *read = r; return MF.S_OK; }
            catch (Exception e) { return Hr(e); }
        }

        private uint ReadNow(byte* b, uint n)
        {
            lock (_lock)
            {
                var span = new Span<byte>(b, (int)n);
                int total = 0;
                while (total < span.Length) { int k = File.Read(span.Slice(total)); if (k <= 0) break; total += k; }
                return (uint)total;
            }
        }

        private uint WriteNow(byte* b, uint n)
        {
            lock (_lock) { File.Write(new ReadOnlySpan<byte>(b, (int)n)); return n; }
        }

        [UnmanagedCallersOnly]
        private static int Write(IntPtr self, byte* b, uint n, uint* written)
        {
            if (S(self) is not { } s || b == null) return MF.E_POINTER;
            try { uint w = s.WriteNow(b, n); if (written != null) *written = w; return MF.S_OK; }
            catch (Exception e) { return Hr(e); }
        }

        /// <summary>The transfer is done at once; the callback is called on the thread pool with its result.</summary>
        private static int Begin(IntPtr self, byte* b, uint n, IntPtr callback, IntPtr state, bool write)
        {
            if (S(self) is not { } s || b == null || callback == IntPtr.Zero) return MF.E_POINTER;
            var result = new MFAsyncResult(IntPtr.Zero, callback, state);
            try { result.Transferred = write ? s.WriteNow(b, n) : s.ReadNow(b, n); }
            catch (Exception e) { result.Status = Hr(e); }
            result.InvokeLater();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int BeginRead(IntPtr self, byte* b, uint n, IntPtr callback, IntPtr state) => Begin(self, b, n, callback, state, false);
        [UnmanagedCallersOnly] private static int BeginWrite(IntPtr self, byte* b, uint n, IntPtr callback, IntPtr state) => Begin(self, b, n, callback, state, true);

        [UnmanagedCallersOnly]
        private static int EndTransfer(IntPtr self, IntPtr result, uint* n)
        {
            if (From<MFAsyncResult>(result) is not { } r) return MF.E_INVALIDARG;
            if (n != null) *n = r.Transferred;
            return r.Status;
        }

        // msoBegin 0, msoCurrent 1
        [UnmanagedCallersOnly]
        private static int Seek(IntPtr self, int origin, long offset, uint flags, ulong* position)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try
            {
                lock (s._lock)
                {
                    s.File.Position = origin == 1 ? s.File.Position + offset : offset;
                    if (position != null) *position = (ulong)s.File.Position;
                }
                return MF.S_OK;
            }
            catch (Exception e) { return Hr(e); }
        }

        [UnmanagedCallersOnly] private static int Flush(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s.File.Flush(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int Close(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s.File.Dispose(); return MF.S_OK; }

        internal static int Hr(Exception e) => e switch
        {
            FileNotFoundException => unchecked((int)0x80070002),
            DirectoryNotFoundException => unchecked((int)0x80070003),
            UnauthorizedAccessException => unchecked((int)0x80070005),
            ObjectDisposedException => MF.MF_E_SHUTDOWN,
            IOException io when io.HResult != 0 => io.HResult,
            _ => MF.E_FAIL,
        };
    }

    /// <summary>IMFAsyncResult: an operation's status and object, and the callback it is for.</summary>
    internal sealed unsafe class MFAsyncResult : ComObject
    {
        public static readonly Guid IID_IMFAsyncResult = new("ac6b7889-0740-4d51-8619-905994a55cc6");
        private static readonly Vtable s_vt = Build();
        private readonly IntPtr _object, _callback, _state;      // AddRef'd
        public int Status;
        public uint Transferred;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFAsyncResult", 8, IID_IMFAsyncResult);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetState);
            vt.Set(4, (delegate* unmanaged<IntPtr, int>)&GetStatus);
            vt.Set(5, (delegate* unmanaged<IntPtr, int, int>)&SetStatus);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetObject);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr>)&GetStateNoAddRef);
            return vt;
        }

        public MFAsyncResult(IntPtr obj, IntPtr callback, IntPtr state) : base(s_vt)
        {
            _object = obj; _callback = callback; _state = state;
            Com.AddRef(obj); Com.AddRef(callback); Com.AddRef(state);
        }

        protected override void OnFinalRelease() { Com.Release(_object); Com.Release(_callback); Com.Release(_state); }

        /// <summary>IMFAsyncCallback::Invoke (slot 4) on the thread pool; the result is released after.</summary>
        public void InvokeLater()
        {
            IntPtr me = Pointer;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(_callback, 4))(_callback, me); }
                finally { Release(); }
            }, null);
        }

        [UnmanagedCallersOnly] private static int GetState(IntPtr self, IntPtr* s) { if (From<MFAsyncResult>(self) is not { } r || s == null) return MF.E_POINTER; *s = r._state; if (r._state == IntPtr.Zero) return MF.E_POINTER; Com.AddRef(r._state); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetStatus(IntPtr self) => From<MFAsyncResult>(self)?.Status ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int SetStatus(IntPtr self, int hr) { if (From<MFAsyncResult>(self) is not { } r) return MF.E_POINTER; r.Status = hr; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetObject(IntPtr self, IntPtr* o) { if (From<MFAsyncResult>(self) is not { } r || o == null) return MF.E_POINTER; *o = r._object; if (r._object == IntPtr.Zero) return MF.E_POINTER; Com.AddRef(r._object); return MF.S_OK; }
        [UnmanagedCallersOnly] private static IntPtr GetStateNoAddRef(IntPtr self) => From<MFAsyncResult>(self)?._state ?? IntPtr.Zero;
    }

    /// <summary>The bytes of an IMFByteStream of the application's, through its vtable.</summary>
    internal sealed unsafe class ForeignByteSource : IByteSource
    {
        private readonly IntPtr _stream;          // AddRef'd
        private readonly object _lock = new();

        public ForeignByteSource(IntPtr stream)
        {
            _stream = stream;
            Com.AddRef(stream);
            ulong l;
            Length = ((delegate* unmanaged<IntPtr, ulong*, int>)Com.Fn(stream, 4))(stream, &l) >= 0 ? (long)l : 0;
        }

        public long Length { get; }

        public int Read(long offset, Span<byte> into)
        {
            lock (_lock)
            {
                if (((delegate* unmanaged<IntPtr, ulong, int>)Com.Fn(_stream, 7))(_stream, (ulong)offset) < 0) return 0;
                int total = 0;
                fixed (byte* p = into)
                    while (total < into.Length)
                    {
                        uint got;
                        if (((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Com.Fn(_stream, 9))(_stream, p + total, (uint)(into.Length - total), &got) < 0 || got == 0) break;
                        total += (int)got;
                    }
                return total;
            }
        }

        public void Dispose() => Com.Release(_stream);
    }
}
