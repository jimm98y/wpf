// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Linux backend for NativePlatform: builds a wgpu surface from an X11 window. The
// native handle is treated as an X11 Window (XID); the display is the process's
// default (XOpenDisplay(NULL)). Wayland is a future sibling. Present-layered has no
// Linux path (see NativePlatform). Provided so the abstraction is complete on every
// platform; not yet run on a Linux box from this dev environment.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static unsafe class LinuxInterop
    {
        public static IntPtr CreateSurface(IntPtr instance, IntPtr x11Window)
        {
            if (x11Window == IntPtr.Zero) return IntPtr.Zero;

            IntPtr display = XOpenDisplay(null);
            if (display == IntPtr.Zero) return IntPtr.Zero;

            var xlibSource = new Wgpu.WGPUSurfaceSourceXlibWindow
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceXlibWindow },
                display = (void*)display,
                window = (ulong)x11Window,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&xlibSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
    }
}
