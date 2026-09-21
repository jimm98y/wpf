// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An unmanaged buffer with a lifetime, which is what every winspool call in ReachFramework passes
// and receives.
//
// This type belongs to the printing stack's Win32 layer and was declared -- but never implemented
// -- in this port. The reference assembly ReachFramework compiles against has it, so
// WinSpoolPrinterCapabilities, FallbackPTProvider and PTProvider all build; the implementation
// assembly did not, so the first call into any of them died with
//
//     TypeLoadException: Could not load type 'MS.Internal.PrintWin32Thunk.SafeMemoryHandle'
//
// which is the failure mode a missing type produces: not at build, not at load, but at the moment
// the first printer is asked what paper it has. Everything else those files need -- HGlobalBuffer,
// PRINTER_INFO_2, DevMode -- is real and lives in ReachFramework. This was the only hole, and with
// it filled the Windows PrintTicket and PrintCapabilities providers work.
//
// A SafeHandle rather than an IntPtr because these buffers are handed to P/Invoke while a print
// dialog is open and a finalizer may run: SafeHandle is what keeps the memory alive across the call
// and frees it exactly once afterwards.
//

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.PrintWin32Thunk
{
    internal sealed class SafeMemoryHandle : SafeHandle
    {
        /// <summary>
        /// Bytes this handle owns, or zero when it does not own them.
        ///
        /// Not readable back from the allocator, so it is remembered. Callers use it to bound a
        /// copy, and a wrong answer here is a buffer overrun rather than an exception.
        /// </summary>
        private readonly int _size;

        /// <summary>
        /// Whether releasing this handle frees anything.
        ///
        /// False for a wrapped pointer. Wrap exists so a pointer somebody else owns can be passed
        /// to a P/Invoke declared in terms of this type -- CreateStreamOnHGlobal takes a null one
        /// to mean "allocate your own" -- and freeing that would be freeing memory twice or
        /// freeing memory that was never allocated.
        /// </summary>
        private readonly bool _owned;

        private SafeMemoryHandle(IntPtr pointer, int size, bool owned)
            : base(IntPtr.Zero, ownsHandle: owned)
        {
            _size = size;
            _owned = owned;

            SetHandle(pointer);
        }

        /// <summary>Wraps a null pointer. Passed where an API reads null as "not supplied".</summary>
        public SafeMemoryHandle(IntPtr win32Pointer) : this(win32Pointer, 0, owned: false)
        {
        }

        /// <summary>
        /// A handle over nothing.
        ///
        /// A fresh instance each time rather than a shared one. A SafeHandle is disposable and the
        /// callers here dispose what they are given; a static singleton would be disposed by the
        /// first caller and closed for every later one, which surfaces as
        /// ObjectDisposedException from the second print dialog a process opens.
        /// </summary>
        public static SafeMemoryHandle Null => new SafeMemoryHandle(IntPtr.Zero, 0, owned: false);

        /// <summary>
        /// Invalid means null HERE, not the -1 a file handle uses. These are memory pointers, and a
        /// failed allocation returns zero.
        /// </summary>
        public override bool IsInvalid => handle == IntPtr.Zero;

        public int Size => _size;

        /// <summary>Allocates a buffer. Throws OutOfMemoryException if it cannot.</summary>
        public static SafeMemoryHandle Create(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

            if (byteCount == 0) return Null;

            return new SafeMemoryHandle(Marshal.AllocHGlobal(byteCount), byteCount, owned: true);
        }

        /// <summary>
        /// Allocates a buffer, answering false instead of throwing.
        ///
        /// The capability path asks the driver how much room it needs and then allocates it, and a
        /// driver that answers with a preposterous number should make the query fail rather than
        /// take the process down.
        /// </summary>
        public static bool TryCreate(int byteCount, ref SafeMemoryHandle result)
        {
            try
            {
                result = Create(byteCount);
                return !result.IsInvalid || byteCount == 0;
            }
            catch (OutOfMemoryException)
            {
                result = Null;
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                result = Null;
                return false;
            }
        }

        /// <summary>Wraps a pointer this handle does not own and will not free.</summary>
        public static SafeMemoryHandle Wrap(IntPtr win32Pointer)
            => new SafeMemoryHandle(win32Pointer, 0, owned: false);

        public void CopyFromArray(byte[] source, int startIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (length == 0) return;

            ThrowIfOutOfRange(source.Length, startIndex, length);
            Marshal.Copy(source, startIndex, handle, length);
        }

        public void CopyToArray(byte[] destination, int startIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (length == 0) return;

            ThrowIfOutOfRange(destination.Length, startIndex, length);
            Marshal.Copy(handle, destination, startIndex, length);
        }

        protected override bool ReleaseHandle()
        {
            if (_owned && handle != IntPtr.Zero) Marshal.FreeHGlobal(handle);

            SetHandle(IntPtr.Zero);
            return true;
        }

        /// <summary>
        /// Both ends of the copy checked, not just the array.
        ///
        /// The unmanaged side has no bounds of its own to check against, so the owned size is the
        /// only thing standing between a driver reporting the wrong length and a heap corruption.
        /// A wrapped pointer has no known size and is trusted, which is the bargain Wrap makes.
        /// </summary>
        private void ThrowIfOutOfRange(int arrayLength, int startIndex, int length)
        {
            ObjectDisposedException.ThrowIf(IsInvalid, this);

            ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex + length, arrayLength);

            if (_owned) ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _size);
        }
    }
}
