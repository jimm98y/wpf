// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser windowing backend for NativePlatform: a WPF "window handle" on the
// browser is an integer canvas handle registered (by the windowing layer or the
// app bootstrap) in globalThis.__wpfCanvases. The presentable wgpu surface is
// the canvas's 'webgpu' context, held in the JS handle table.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Browser;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal static class BrowserInterop
    {
        public static IntPtr CreateSurface(IntPtr instance, IntPtr canvasHandle)
        {
            int surface = WgpuBrowserJs.CreateSurface((int)canvasHandle);
            if (surface == 0)
                throw new InvalidOperationException(
                    $"Could not create a WebGPU canvas surface for handle 0x{canvasHandle:x} " +
                    "(is the canvas registered in globalThis.__wpfCanvases?).");
            return (IntPtr)surface;
        }
    }
}
