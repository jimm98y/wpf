// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The callbacks handed to webgpu.h -- a buffer map's, an error scope's -- are [UnmanagedCallersOnly]
// functions, which wgpu-native calls through their pointers. Each also has a managed twin, registered
// here under the same pointer, for a caller that must not call the pointer: the browser's bridge
// (Browser/Wgpu.Browser.cs), on Mono, whose calli into an [UnmanagedCallersOnly] function passes a struct
// argument wrongly -- the WGPUStringView these take by value arrived shifted, its user data garbage, and
// the GCHandle read from it brought the runtime down. Natively the twins are registered and never used.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        internal delegate void MapTwin(WGPUMapAsyncStatus status, WGPUStringView message, IntPtr userdata1, IntPtr userdata2);

        internal delegate void ScopeTwin(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, IntPtr userdata1, IntPtr userdata2);

        private static readonly Dictionary<IntPtr, Delegate> s_twins = new();

        /// <summary>A WGPUBufferMapCallback: <paramref name="callback"/>, with <paramref name="twin"/> the same body.</summary>
        internal static IntPtr MapCallback(delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus, WGPUStringView, IntPtr, IntPtr, void> callback, MapTwin twin)
            => Twin((IntPtr)callback, twin);

        /// <summary>A WGPUPopErrorScopeCallback: <paramref name="callback"/>, with <paramref name="twin"/> the same body.</summary>
        internal static IntPtr ScopeCallback(delegate* unmanaged[Cdecl]<WGPUPopErrorScopeStatus, WGPUErrorType, WGPUStringView, IntPtr, IntPtr, void> callback, ScopeTwin twin)
            => Twin((IntPtr)callback, twin);

        private static IntPtr Twin(IntPtr callback, Delegate twin)
        {
            lock (s_twins) s_twins.TryAdd(callback, twin);
            return callback;
        }

        /// <summary>The managed twin registered for <paramref name="callback"/>.</summary>
        internal static T? TwinOf<T>(IntPtr callback) where T : Delegate
        {
            lock (s_twins) return s_twins.TryGetValue(callback, out Delegate? d) ? d as T : null;
        }
    }
}
