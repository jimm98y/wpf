// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The exports of the d3d11 / dxgi shim (Documentation/d3d11-webgpu-shim.md, section 1): what an app's
// [DllImport("d3d11.dll")] reaches through the launcher's export images (Windows) or libwin32shim
// (elsewhere). The launcher finds Register by name and points the exports at these functions.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static class Win32Events
    {
        [DllImport("kernel32", EntryPoint = "SetEvent", ExactSpelling = true)]
        private static extern int SetEventNative(IntPtr handle);

        /// <summary>Signals a Windows event handle (DXGI's EnqueueSetEvent). Off Windows an event is the
        /// Win32 shim's own object and is signalled through it.</summary>
        public static void Set(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            if (OperatingSystem.IsWindows()) SetEventNative(handle);
            else s_setEvent?.Invoke(handle);
        }

        internal static Action<IntPtr>? s_setEvent;
    }

    internal static unsafe class Exports
    {
        private const int D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_DEBUG = 0x2;

        /// <summary>For the launcher: registers every export it can route here. <paramref name="add"/>
        /// takes (module, export name, function pointer).</summary>
        public static int Register(Action<string, string, IntPtr> add, Action<string, string>? logOnce)
        {
            if (logOnce != null) D3DLog.LogOnce = logOnce;
            add("d3d11", "D3D11CreateDevice", (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, uint, uint*, uint, uint, IntPtr*, uint*, IntPtr*, int>)&D3D11CreateDevice);
            add("dxgi", "CreateDXGIFactory", (IntPtr)(delegate* unmanaged<Guid*, IntPtr*, int>)&CreateDXGIFactory);
            add("dxgi", "CreateDXGIFactory1", (IntPtr)(delegate* unmanaged<Guid*, IntPtr*, int>)&CreateDXGIFactory1);
            add("dxgi", "CreateDXGIFactory2", (IntPtr)(delegate* unmanaged<uint, Guid*, IntPtr*, int>)&CreateDXGIFactory2);
            add("dxgi", "DXGIGetDebugInterface1", (IntPtr)(delegate* unmanaged<uint, Guid*, IntPtr*, int>)&DXGIGetDebugInterface1);
            return 5;
        }

        /// <summary>The managed entry point, for callers in this process (tests): the same function
        /// the export reaches.</summary>
        internal static delegate* unmanaged<IntPtr, uint, IntPtr, uint, uint*, uint, uint, IntPtr*, uint*, IntPtr*, int> CreateDevicePointer => &D3D11CreateDevice;
        internal static delegate* unmanaged<Guid*, IntPtr*, int> CreateFactory1Pointer => &CreateDXGIFactory1;

        private static readonly D3D_FEATURE_LEVEL[] s_defaultLevels = { D3D_FEATURE_LEVEL.L11_0, D3D_FEATURE_LEVEL.L10_1, D3D_FEATURE_LEVEL.L10_0, D3D_FEATURE_LEVEL.L9_3, D3D_FEATURE_LEVEL.L9_2, D3D_FEATURE_LEVEL.L9_1 };

        [UnmanagedCallersOnly]
        private static int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint levelCount, uint sdk, IntPtr* device, uint* level, IntPtr* context)
        {
            if (device != null) *device = IntPtr.Zero;
            if (context != null) *context = IntPtr.Zero;
            try
            {
                // the highest requested level the shim offers (11_1 at most)
                D3D_FEATURE_LEVEL chosen = 0;
                if (levels == null || levelCount == 0) chosen = s_defaultLevels[0];
                else
                    for (int i = 0; i < levelCount; i++)
                    {
                        var l = (D3D_FEATURE_LEVEL)levels[i];
                        if (l <= D3D_FEATURE_LEVEL.L11_1 && l > chosen) chosen = l;
                    }
                if (chosen == 0) return HResult.E_INVALIDARG;
                if (level != null) *level = (uint)chosen;
                if (device == null && context == null) return HResult.S_FALSE;   // a query of the level

                DxgiAdapter? a = ComObject.From<DxgiAdapter>(adapter);
                var gpu = D3DDevice.CreateGpu();
                var d = new D3DDevice(gpu, ownsGpu: true, chosen, flags & ~D3D11_CREATE_DEVICE_DEBUG, a);
                if ((flags & D3D11_CREATE_DEVICE_DEBUG) != 0) D3DLog.Once("debugdev", "a debug device was asked for; it is an ordinary one");
                D3DLog.Once("created", $"D3D11 device at feature level {chosen} over {gpu.AdapterDescription}");
                if (context != null) *context = d.Immediate.AddRefPointer();
                if (device != null) *device = d.Pointer;
                else d.Release();
                return HResult.S_OK;
            }
            catch (Exception e)
            {
                D3DLog.Error("D3D11CreateDevice", e);
                return HResult.E_FAIL;
            }
        }

        private static int Factory(Guid* riid, IntPtr* pp)
        {
            if (pp == null || riid == null) return HResult.E_INVALIDARG;
            *pp = IntPtr.Zero;
            try
            {
                var f = DxgiFactory.Create();
                int hr = D3DDevice.QueryInterfaceOf(f, *riid, pp);
                f.Release();
                return hr;
            }
            catch (Exception e) { D3DLog.Error("CreateDXGIFactory", e); return HResult.E_FAIL; }
        }

        [UnmanagedCallersOnly] private static int CreateDXGIFactory(Guid* riid, IntPtr* pp) => Factory(riid, pp);
        [UnmanagedCallersOnly] private static int CreateDXGIFactory1(Guid* riid, IntPtr* pp) => Factory(riid, pp);
        [UnmanagedCallersOnly] private static int CreateDXGIFactory2(uint flags, Guid* riid, IntPtr* pp) => Factory(riid, pp);

        [UnmanagedCallersOnly]
        private static int DXGIGetDebugInterface1(uint flags, Guid* riid, IntPtr* pp)
        {
            if (pp != null) *pp = IntPtr.Zero;
            return HResult.E_NOINTERFACE;              // as on a machine without the graphics tools
        }
    }
}
