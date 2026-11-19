// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A COM IStream as a Stream, for MFCreateMFByteStreamOnStream (Documentation/media-foundation-shim.md, "What there
// is"): read, written and sought through its own vtable (Read 3, Write 4, Seek 5, SetSize 6, Commit 8, Stat 12).
//

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed unsafe class ComStream : Stream
    {
        private IntPtr _s;                     // IStream, AddRef'd

        public ComStream(IntPtr stream)
        {
            Com.AddRef(stream); _s = stream;
            // STATSTG's grfMode: STGM_READ 0, STGM_WRITE 1, STGM_READWRITE 2 -- what the byte stream says it can do
            var stat = stackalloc byte[80];
            new Span<byte>(stat, 80).Clear();
            uint mode = ((delegate* unmanaged<IntPtr, byte*, uint, int>)Com.Fn(_s, 12))(_s, stat, 1) >= 0 ? *(uint*)(stat + 48) & 3 : 2;
            _read = mode != 1; _write = mode is 1 or 2;
        }

        private readonly bool _read, _write;
        public override bool CanRead => _read;
        public override bool CanWrite => _write;
        public override bool CanSeek => true;

        /// <summary>STATSTG's cbSize (STATFLAG_NONAME): past pwcsName and type.</summary>
        public override long Length
        {
            get
            {
                var stat = stackalloc byte[80];
                new Span<byte>(stat, 80).Clear();
                return ((delegate* unmanaged<IntPtr, byte*, uint, int>)Com.Fn(_s, 12))(_s, stat, 1) >= 0 ? *(long*)(stat + 16) : 0;
            }
        }

        public override long Position { get => Seek(0, SeekOrigin.Current); set => Seek(value, SeekOrigin.Begin); }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ulong at;
            int hr = ((delegate* unmanaged<IntPtr, long, uint, ulong*, int>)Com.Fn(_s, 5))(_s, offset, (uint)origin, &at);
            if (hr < 0) throw new IOException($"IStream::Seek: 0x{hr:x8}");
            return (long)at;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> into)
        {
            uint got;
            int hr;
            fixed (byte* p = into) hr = ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Com.Fn(_s, 3))(_s, p, (uint)into.Length, &got);
            if (hr < 0) throw new IOException($"IStream::Read: 0x{hr:x8}");
            return (int)got;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            uint wrote;
            int hr;
            fixed (byte* p = data) hr = ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Com.Fn(_s, 4))(_s, p, (uint)data.Length, &wrote);
            if (hr < 0 || wrote != data.Length) throw new IOException($"IStream::Write: 0x{hr:x8}");
        }

        public override void SetLength(long value) => ((delegate* unmanaged<IntPtr, ulong, int>)Com.Fn(_s, 6))(_s, (ulong)value);
        public override void Flush() => ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(_s, 8))(_s, 0);          // Commit(STGC_DEFAULT)

        protected override void Dispose(bool disposing)
        {
            Com.Release(_s);
            _s = IntPtr.Zero;
            base.Dispose(disposing);
        }
    }
}
