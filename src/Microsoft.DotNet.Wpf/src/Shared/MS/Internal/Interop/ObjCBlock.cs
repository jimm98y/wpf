// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Objective-C blocks, from a runtime that has none.
//
// A block is not a function pointer: it is a heap object whose first fields are an isa, some flags
// and only THEN the code pointer, and Apple's asynchronous APIs take nothing else. The Cocoa and
// UIKit backends in this assembly had managed to avoid them entirely -- every call they make is
// synchronous, and the one completion handler among them (UIKitPrint) passes nil. NSItemProvider
// ends that: reading a dropped item is `loadDataRepresentationForTypeIdentifier:completionHandler:`
// and there is no synchronous form of it, so a drop cannot be read without one of these.
//
// What is built here is a GLOBAL block. The distinction matters and is the reason this is only
// sixty lines: a stack block must supply copy and dispose helpers, because the callee will
// Block_copy it onto the heap and the copy has to bring the captured variables along. Block_copy of
// a global block is defined to return the same pointer untouched, so the block this hands out is
// the one that comes back, no helpers are needed, and its lifetime is ours to manage -- which is
// exactly what a caller waiting on a completion needs anyway.
//
// The capture, then, cannot live in a C closure; it rides in a field appended after the descriptor.
// The invoke function is a static [UnmanagedCallersOnly] (iOS is AOT-only and cannot make a
// native->managed thunk at run time) whose first argument is the block itself, so it reads its
// context straight back out of that.
//

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    internal static unsafe class ObjCBlock
    {
        // The layout every Objective-C block starts with, plus one field of our own. Anything after
        // `Descriptor` is the block's captured state as far as the runtime is concerned, and it never
        // looks at it -- only the copy/dispose helpers a global block does not have would.
        [StructLayout(LayoutKind.Sequential)]
        private struct Literal
        {
            public IntPtr Isa;
            public int Flags;
            public int Reserved;
            public IntPtr Invoke;
            public IntPtr Descriptor;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockDescriptor
        {
            public nuint Reserved;
            public nuint Size;
        }

        /// <summary>BLOCK_IS_GLOBAL: tells Block_copy/Block_release to leave this block alone.</summary>
        private const int BlockIsGlobal = 1 << 28;

        private static IntPtr s_globalBlockIsa;
        private static IntPtr s_descriptor;

        /// <summary>
        ///  Wraps an invoke function as a block. Returns Zero when blocks are unavailable, which on a
        ///  non-Apple platform they always are.
        /// </summary>
        /// <param name="invoke">
        ///  A static <c>[UnmanagedCallersOnly]</c> function pointer whose FIRST parameter is the block
        ///  itself and whose remaining parameters are the block's own. Passing anything else -- a
        ///  managed delegate, a plain C function expecting no block argument -- misreads every
        ///  argument by one position.
        /// </param>
        /// <param name="context">Handed back by <see cref="ContextOf"/> from inside the invoke.</param>
        /// <remarks>
        ///  The caller owns the result and must <see cref="Release"/> it once the block can no longer
        ///  be called. There is no reference counting to lean on here: a global block is never copied
        ///  and never released by the runtime, which is the whole reason it is safe to hand out.
        /// </remarks>
        public static IntPtr Create(IntPtr invoke, IntPtr context)
        {
            if (invoke == IntPtr.Zero) return IntPtr.Zero;

            IntPtr isa = GlobalBlockIsa();
            if (isa == IntPtr.Zero) return IntPtr.Zero;

            IntPtr descriptor = SharedDescriptor();
            if (descriptor == IntPtr.Zero) return IntPtr.Zero;

            var block = (Literal*)NativeMemory.Alloc((nuint)sizeof(Literal));
            block->Isa = isa;
            block->Flags = BlockIsGlobal;
            block->Reserved = 0;
            block->Invoke = invoke;
            block->Descriptor = descriptor;
            block->Context = context;
            return (IntPtr)block;
        }

        /// <summary>The context <see cref="Create"/> was given, read from inside the invoke.</summary>
        public static IntPtr ContextOf(IntPtr block)
            => block == IntPtr.Zero ? IntPtr.Zero : ((Literal*)block)->Context;

        /// <summary>Frees a block from <see cref="Create"/>. Calling it afterwards is a use-after-free.</summary>
        public static void Release(IntPtr block)
        {
            if (block != IntPtr.Zero) NativeMemory.Free((void*)block);
        }

        // The descriptor is immutable and identical for every block this makes (same size, no
        // helpers), so one is shared. It is never freed, and is a handful of bytes.
        private static IntPtr SharedDescriptor()
        {
            if (s_descriptor != IntPtr.Zero) return s_descriptor;

            var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
            descriptor->Reserved = 0;
            descriptor->Size = (nuint)sizeof(Literal);
            return s_descriptor = (IntPtr)descriptor;
        }

        // _NSConcreteGlobalBlock is a data symbol in libSystem, not a function, so its ADDRESS is
        // what an isa points at. dlsym returns exactly that.
        private static IntPtr GlobalBlockIsa()
        {
            if (s_globalBlockIsa != IntPtr.Zero) return s_globalBlockIsa;

            try { s_globalBlockIsa = dlsym(RtldDefault, "_NSConcreteGlobalBlock"); }
            catch (DllNotFoundException) { s_globalBlockIsa = IntPtr.Zero; }
            catch (EntryPointNotFoundException) { s_globalBlockIsa = IntPtr.Zero; }
            return s_globalBlockIsa;
        }

        /// <summary>RTLD_DEFAULT: search every loaded image, which is where libSystem already is.</summary>
        private static readonly IntPtr RtldDefault = new IntPtr(-2);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
    }
}
