// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sink writer's output to a byte stream of the application's (Documentation/media-foundation-shim.md,
// "The sink writer"): a Stream over its IMFByteStream -- written at its position, which is set to go back (the
// mdat's size), and closed when the writer finishes, as Windows' MPEG-4 sink closes it.
//

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed unsafe class ByteStreamWriter : Stream
    {
        private IntPtr _stream;                // IMFByteStream, AddRef'd

        public ByteStreamWriter(IntPtr stream) { Com.AddRef(stream); _stream = stream; }

        /// <summary>MFBYTESTREAM_IS_WRITABLE and _IS_SEEKABLE, as Windows' sink needs them at BeginWriting.</summary>
        public bool Writable
        {
            get
            {
                uint caps;
                return ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(_stream, 3))(_stream, &caps) >= 0 && (caps & 6) == 6;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;

        public override long Length { get { ulong l; return ((delegate* unmanaged<IntPtr, ulong*, int>)Com.Fn(_stream, 4))(_stream, &l) >= 0 ? (long)l : 0; } }

        public override long Position
        {
            get { ulong p; return ((delegate* unmanaged<IntPtr, ulong*, int>)Com.Fn(_stream, 6))(_stream, &p) >= 0 ? (long)p : 0; }
            set
            {
                int hr = ((delegate* unmanaged<IntPtr, ulong, int>)Com.Fn(_stream, 7))(_stream, (ulong)value);
                if (hr < 0) throw new IOException($"the byte stream's SetCurrentPosition: 0x{hr:x8}");
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <summary>As Windows' sink writes: BeginWrite, the callback waited for, EndWrite; then GetCurrentPosition.</summary>
        public override void Write(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                int done = 0;
                while (done < data.Length)
                {
                    var cb = new WriteCallback();
                    try
                    {
                        int hr = ((delegate* unmanaged<IntPtr, byte*, uint, IntPtr, IntPtr, int>)Com.Fn(_stream, 13))(_stream, p + done, (uint)(data.Length - done), cb.Pointer, IntPtr.Zero);
                        uint wrote;
                        if (hr == MF.E_NOTIMPL)
                        {
                            // a stream of no asynchronous writes: written at once
                            hr = ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Com.Fn(_stream, 12))(_stream, p + done, (uint)(data.Length - done), &wrote);
                            if (hr < 0) throw new IOException($"the byte stream's Write: 0x{hr:x8}");
                        }
                        else
                        {
                            if (hr < 0) throw new IOException($"the byte stream's BeginWrite: 0x{hr:x8}");
                            if (!cb.Done.Wait(30_000)) throw new IOException("the byte stream's BeginWrite never called back");
                            hr = ((delegate* unmanaged<IntPtr, IntPtr, uint*, int>)Com.Fn(_stream, 14))(_stream, cb.Result, &wrote);
                            if (hr < 0) throw new IOException($"the byte stream's EndWrite: 0x{hr:x8}");
                        }
                        if (wrote == 0) throw new IOException("the byte stream wrote nothing");
                        done += (int)wrote;
                        _ = Position;
                    }
                    finally { cb.Release(); }
                }
            }
        }

        /// <summary>IMFAsyncCallback of one write: its result kept (AddRef'd), the event set.</summary>
        private sealed class WriteCallback : ComObject
        {
            private static readonly Vtable s_vt = Build();
            public readonly System.Threading.ManualResetEventSlim Done = new();
            public IntPtr Result;
            public WriteCallback() : base(s_vt) { }
            protected override void OnFinalRelease() { Com.Release(Result); Done.Dispose(); }
            private static Vtable Build()
            {
                var vt = new Vtable("IMFAsyncCallback", 5, new Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"));
                vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetParameters);
                vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, int>)&Invoke);
                return vt;
            }
            [System.Runtime.InteropServices.UnmanagedCallersOnly] private static int GetParameters(IntPtr self, uint* flags, uint* queue) => MF.E_NOTIMPL;
            [System.Runtime.InteropServices.UnmanagedCallersOnly]
            private static int Invoke(IntPtr self, IntPtr result)
            {
                if (From<WriteCallback>(self) is not { } c) return MF.E_POINTER;
                Com.AddRef(result);
                c.Result = result;
                c.Done.Set();
                return MF.S_OK;
            }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset };
        public override void SetLength(long value) => ((delegate* unmanaged<IntPtr, ulong, int>)Com.Fn(_stream, 5))(_stream, (ulong)value);

        protected override void Dispose(bool disposing)
        {
            if (_stream == IntPtr.Zero) return;
            ((delegate* unmanaged<IntPtr, int>)Com.Fn(_stream, 17))(_stream);          // Close
            Com.Release(_stream);
            _stream = IntPtr.Zero;
            base.Dispose(disposing);
        }
    }
}
