// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's vtable functions with more than eight parameters (IDirect3DDevice9::CreateTexture,
// CreateRenderTarget, ID3D11DeviceContext::CopySubresourceRegion, ...), as managed twins. Mono's
// interpreter enters an [UnmanagedCallersOnly] function of more than MAX_INTERP_ENTRY_ARGS (8) arguments
// through its general entry, whose signature the generated native-to-interp wrapper does not call it
// with: in the browser any call of one through its pointer stopped the runtime ("function signature
// mismatch"), and Ab3d.DXEngine's first IDirect3DDevice9::CreateTexture was one. Each such function
// registers a delegate of the same body here, its pointer arguments as IntPtr, under its own pointer;
// a caller that can (WpfWebGpu.ComInterop's Callbacks, making a delegate of a vtable slot) calls the twin
// instead -- found through AppContext's "WpfWebGpu.NativeTwin". Anywhere else the pointer works as well.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static unsafe class NativeTwins
    {
        private static readonly Dictionary<IntPtr, Delegate> s_twins = new();

        static NativeTwins()
        {
            AppContext.SetData("WpfWebGpu.NativeTwin", (Func<IntPtr, Delegate?>)Of);
        }

        /// <summary>Registers <paramref name="twin"/> for the function <paramref name="function"/>; returns the function.</summary>
        internal static void* Register(IntPtr function, Delegate twin)
        {
            lock (s_twins) s_twins[function] = twin;
            return (void*)function;
        }

        internal static Delegate? Of(IntPtr function)
        {
            lock (s_twins) return s_twins.TryGetValue(function, out Delegate? d) ? d : null;
        }
    }
}
