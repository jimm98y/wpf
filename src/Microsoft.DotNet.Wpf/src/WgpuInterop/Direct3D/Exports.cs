// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The exports of the d3d11 / dxgi shim (Documentation/d3d11-webgpu-shim.md, section 1): what an app's
// [DllImport("d3d11.dll")] reaches, rewritten by wpfrun's P/Invoke rewriter (Documentation/
// pinvoke-rewriter.md) into a call of the method tagged with its name.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>The exports a method implements, for the P/Invoke rewriter (Documentation/pinvoke-rewriter.md;
    /// matched by name, as WpfWebGpu.Win32's).</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    internal sealed class Win32ExportAttribute : Attribute
    {
        public Win32ExportAttribute(params string[] names) => Names = names;
        public string[] Names { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    internal sealed class Win32ExportOnWindowsAttribute : Attribute
    {
        public Win32ExportOnWindowsAttribute(int mask, params string[] names) { Mask = mask; Names = names; }
        public int Mask { get; }
        public string[] Names { get; }
    }

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
            else (s_setEvent ??= AppContext.GetData("WpfWebGpu.SetEvent") as Action<IntPtr>)?.Invoke(handle);
        }

        /// <summary>The Win32 shim's SetEvent (WpfWebGpu.Win32 publishes it).</summary>
        private static Action<IntPtr>? s_setEvent;
    }

    public static unsafe class Exports
    {
        // Every call, on Windows too (the user's decision, 2026-10-19: one implementation everywhere).
        private const int Always = 1 << 15;

        private const int D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_DEBUG = 0x2;

        // d3dcompiler_47: ours on every OS, Windows too (the user's decision, 2026-10-19: one
        // implementation everywhere). Reflection is managed, over the blob's own chunks; run-time
        // HLSL compilation is not there yet (section 9), and answers E_FAIL with one line saying so.

        private static int NoCompiler(byte* entry, byte* target, IntPtr* code, IntPtr* errors)
        {
            if (code != null) *code = IntPtr.Zero;
            if (errors != null) *errors = IntPtr.Zero;
            string what = $"{(entry != null ? Marshal.PtrToStringUTF8((IntPtr)entry) : "?")} ({(target != null ? Marshal.PtrToStringUTF8((IntPtr)target) : "?")})";
            D3DLog.Once("d3dcompile", $"D3DCompile of {what}: the shim has no HLSL compiler yet; precompiled shaders (DXBC) work, run-time HLSL does not (E_FAIL)");
            return unchecked((int)0x80004005);
        }

        [Win32Export("D3DCompile")] [Win32ExportOnWindows(Always, "D3DCompile")]
        public static int D3DCompile(void* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint f1, uint f2, IntPtr* code, IntPtr* errors)
            => NoCompiler(entry, target, code, errors);

        [Win32Export("D3DCompile2")] [Win32ExportOnWindows(Always, "D3DCompile2")]
        public static int D3DCompile2(void* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint f1, uint f2, uint secondaryFlags, void* secondary, nuint secondarySize, IntPtr* code, IntPtr* errors)
            => NoCompiler(entry, target, code, errors);

        [Win32Export("D3DCompileFromFile")] [Win32ExportOnWindows(Always, "D3DCompileFromFile")]
        public static int D3DCompileFromFile(char* file, void* defines, void* include, byte* entry, byte* target, uint f1, uint f2, IntPtr* code, IntPtr* errors)
            => NoCompiler(entry, target, code, errors);

        [Win32Export("D3DReflect")] [Win32ExportOnWindows(Always, "D3DReflect")]
        public static int D3DReflect(void* src, nuint size, Guid* iid, IntPtr* reflector)
        {
            try { return D3DShaderReflection.Reflect(src, size, iid, reflector); }
            catch (Exception e) { D3DLog.Error("D3DReflect", e); if (reflector != null) *reflector = IntPtr.Zero; return HResult.E_FAIL; }
        }

        internal static delegate*<void*, nuint, Guid*, IntPtr*, int> ReflectPointer => &D3DReflect;
        internal static delegate*<void*, nuint, byte*, void*, void*, byte*, byte*, uint, uint, IntPtr*, IntPtr*, int> CompilePointer => &D3DCompile;

        /// <summary>The managed entry point, for callers in this process (tests): the same function
        /// the export reaches.</summary>
        internal static delegate*<IntPtr, uint, IntPtr, uint, uint*, uint, uint, IntPtr*, uint*, IntPtr*, int> CreateDevicePointer => &D3D11CreateDevice;
        internal static delegate*<Guid*, IntPtr*, int> CreateFactory1Pointer => &CreateDXGIFactory1;
        internal static delegate*<uint, IntPtr*, int> Create9ExPointer => &Direct3DCreate9Ex;

        [Win32Export("Direct3DCreate9Ex")] [Win32ExportOnWindows(Always, "Direct3DCreate9Ex")]
        public static int Direct3DCreate9Ex(uint sdk, IntPtr* d3d)
        {
            if (d3d == null) return D3D9Const.D3DERR_INVALIDCALL;
            try { *d3d = Direct3D9.Create(ex: true).Pointer; return HResult.S_OK; }
            catch (Exception e) { D3DLog.Error("Direct3DCreate9Ex", e); *d3d = IntPtr.Zero; return D3D9Const.D3DERR_NOTAVAILABLE; }
        }

        [Win32Export("Direct3DCreate9")] [Win32ExportOnWindows(Always, "Direct3DCreate9")]
        public static IntPtr Direct3DCreate9(uint sdk)
        {
            try { return Direct3D9.Create(ex: false).Pointer; }
            catch (Exception e) { D3DLog.Error("Direct3DCreate9", e); return IntPtr.Zero; }
        }

        private static readonly D3D_FEATURE_LEVEL[] s_defaultLevels = { D3D_FEATURE_LEVEL.L11_0, D3D_FEATURE_LEVEL.L10_1, D3D_FEATURE_LEVEL.L10_0, D3D_FEATURE_LEVEL.L9_3, D3D_FEATURE_LEVEL.L9_2, D3D_FEATURE_LEVEL.L9_1 };

        [Win32Export("D3D11CreateDevice")] [Win32ExportOnWindows(Always, "D3D11CreateDevice")]
        public static int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint levelCount, uint sdk, IntPtr* device, uint* level, IntPtr* context)
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
                // the calling thread's device, its compositors' (Composition/ThreadGpu.cs)
                var gpu = Composition.ThreadGpu.AcquireForShim();
                var d = new D3DDevice(gpu, ownsGpu: true, chosen, flags & ~D3D11_CREATE_DEVICE_DEBUG, a);
                if ((flags & D3D11_CREATE_DEVICE_DEBUG) != 0) D3DLog.Once("debugdev", "a debug device was asked for; it is an ordinary one");
                D3DLog.Once("created", $"D3D11 device at feature level {chosen} over {gpu.AdapterDescription}; optional features 0x{string.Join(" 0x", System.Linq.Enumerable.Select(D3DDevice.Features, f => f.ToString("x")))}");
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

        [Win32Export("CreateDXGIFactory")] [Win32ExportOnWindows(Always, "CreateDXGIFactory")] public static int CreateDXGIFactory(Guid* riid, IntPtr* pp) => Factory(riid, pp);
        [Win32Export("CreateDXGIFactory1")] [Win32ExportOnWindows(Always, "CreateDXGIFactory1")] public static int CreateDXGIFactory1(Guid* riid, IntPtr* pp) => Factory(riid, pp);
        [Win32Export("CreateDXGIFactory2")] [Win32ExportOnWindows(Always, "CreateDXGIFactory2")] public static int CreateDXGIFactory2(uint flags, Guid* riid, IntPtr* pp) => Factory(riid, pp);

        [Win32Export("DXGIGetDebugInterface1")] [Win32ExportOnWindows(Always, "DXGIGetDebugInterface1")]
        public static int DXGIGetDebugInterface1(uint flags, Guid* riid, IntPtr* pp)
        {
            if (pp != null) *pp = IntPtr.Zero;
            return HResult.E_NOINTERFACE;              // as on a machine without the graphics tools
        }
    }
}
