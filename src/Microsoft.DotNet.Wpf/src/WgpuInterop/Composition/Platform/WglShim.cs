// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Two things the official wgpu-native (v29.0.1.1) trips over on a WGL driver that is within its
// rights, measured on the Parallels display driver (prl_gldd, "Parallels using Metal (Apple M4)",
// 2026-10-07). Either one costs the GL backend, and with it the process falls back to D3D12's WARP
// -- every frame rasterized on the CPU.
//
//  1. The driver fails the FIRST wglCreateContext of a process (ERROR_INVALID_PIXEL_FORMAT on an
//     accelerated format) and grants every one after it. wgpu-hal's WGL backend makes exactly one,
//     its "initial" context, and gives up when it fails.
//
//  2. wgpu-hal (wgl.rs, Instance::init) asks wglCreateContextAttribsARB for a core profile WITHOUT
//     a version. The default is then 1.0, the profile mask is ignored below 3.2, and any context
//     compatible with 1.0 is a correct answer: this driver returns 2.1, which wgpu rejects
//     ("Returned GL context is 2.1, when 3.3+ is needed"). Asked for 3.3 or 4.1 it grants them.
//
// Neither is ours to fix in the binary, so until wgpu passes a version (sent upstream), this makes
// one throwaway context before wgpu looks, and points wgpu_native.dll's import of
// opengl32!wglGetProcAddress at a wrapper that hands out a wglCreateContextAttribsARB which adds a
// version when none was asked for: 4.3, else 4.1, else 3.3, else the request as it was. A request
// that names its version is passed through untouched, so nothing changes once wgpu is fixed.
// Windows only; WPF_WEBGPU_WGL_SHIM=0 turns it off.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal static unsafe class WglShim
    {
        private static bool s_installed;
        private static IntPtr s_realGetProcAddress;
        private static IntPtr s_realCreateAttribs;

        private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091, WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;

        /// <summary>Before the first wgpu instance: prime the driver, then patch wgpu_native's import.</summary>
        internal static void Install()
        {
            if (!OperatingSystem.IsWindows() || s_installed) return;
            s_installed = true;
            if (Environment.GetEnvironmentVariable("WPF_WEBGPU_WGL_SHIM") == "0") return;
            try
            {
                PrimeFirstContext();
                // The module the bindings themselves load (through NativeLoad's resolver): binding one
                // of their P/Invokes loads it without calling anything.
                Marshal.Prelink(typeof(Wgpu).GetMethod(nameof(Wgpu.wgpuSetLogLevel),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!);
                IntPtr wgpu = GetModuleHandleW(Wgpu.Library + ".dll");
                if (wgpu == IntPtr.Zero) return;
                IntPtr opengl = NativeLibrary.Load("opengl32.dll");
                s_realGetProcAddress = NativeLibrary.GetExport(opengl, "wglGetProcAddress");
                delegate* unmanaged[Stdcall]<byte*, IntPtr> hook = &GetProcAddressHook;
                PatchImport(wgpu, "opengl32.dll", "wglGetProcAddress", (IntPtr)hook);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr GetProcAddressHook(byte* name)
        {
            IntPtr real = ((delegate* unmanaged[Stdcall]<byte*, IntPtr>)s_realGetProcAddress)(name);
            if (real == IntPtr.Zero || name == null) return real;
            if (MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name).SequenceEqual("wglCreateContextAttribsARB"u8))
            {
                s_realCreateAttribs = real;
                delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, IntPtr> wrap = &CreateContextAttribsHook;
                return (IntPtr)wrap;
            }
            return real;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr CreateContextAttribsHook(IntPtr dc, IntPtr share, int* attribs)
        {
            var real = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, IntPtr>)s_realCreateAttribs;
            int n = 0;
            bool versioned = false;
            if (attribs != null)
                for (; attribs[n] != 0; n += 2)
                    if (attribs[n] == WGL_CONTEXT_MAJOR_VERSION_ARB) versioned = true;
            if (versioned)
                return real(dc, share, attribs);
            // The request with a version in front: the newest of the versions this driver grants.
            int* withVersion = stackalloc int[n + 5];
            for (int i = 0; i < n; i++) withVersion[i + 4] = attribs[i];
            withVersion[n + 4] = 0;
            withVersion[0] = WGL_CONTEXT_MAJOR_VERSION_ARB;
            withVersion[2] = WGL_CONTEXT_MINOR_VERSION_ARB;
            foreach ((int major, int minor) in stackalloc[] { (4, 3), (4, 1), (3, 3) })
            {
                withVersion[1] = major;
                withVersion[3] = minor;
                IntPtr context = real(dc, share, withVersion);
                if (context != IntPtr.Zero) return context;
            }
            return real(dc, share, attribs);
        }

        // ---- the first context ----------------------------------------------------------------

        private static void PrimeFirstContext()
        {
            IntPtr inst = GetModuleHandleW(null);
            IntPtr defProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");
            fixed (char* cls = ClassName)
            {
                var wc = new WndClass { style = CS_OWNDC, proc = defProc, inst = inst, name = cls };
                if (RegisterClassW(&wc) == 0) return;
            }
            IntPtr hwnd = CreateWindowExW(0, ClassName, ClassName, WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
            {
                IntPtr dc = GetDC(hwnd);
                // wgpu-hal's own pixel format (wgl.rs setup_pixel_format).
                var pfd = new Pfd { size = (ushort)sizeof(Pfd), version = 1, flags = 0x4 | 0x20 | 0x1, colorBits = 8 };
                int format = ChoosePixelFormat(dc, &pfd);
                if (format != 0 && SetPixelFormat(dc, format, &pfd))
                {
                    IntPtr rc = wglCreateContext(dc);
                    if (rc != IntPtr.Zero) wglDeleteContext(rc);
                }
                ReleaseDC(hwnd, dc);
                DestroyWindow(hwnd);
            }
            UnregisterClassW(ClassName, inst);
        }

        // ---- the import ------------------------------------------------------------------------

        /// <summary>Points <paramref name="module"/>'s import of <paramref name="dll"/>!<paramref name="function"/>
        /// at <paramref name="replacement"/>, walking its PE import directory (by-name imports only).</summary>
        private static void PatchImport(IntPtr module, string dll, string function, IntPtr replacement)
        {
            byte* b = (byte*)module;
            int pe = *(int*)(b + 0x3C);
            // IMAGE_OPTIONAL_HEADER64: the data directories start at +112; entry 1 is the import table.
            byte* opt = b + pe + 24;
            if (*(ushort*)opt != 0x20B) return;   // PE32+ only: every head this ships to is 64-bit or ARM64
            uint importRva = *(uint*)(opt + 112 + 8);
            if (importRva == 0) return;
            for (byte* desc = b + importRva; *(uint*)(desc + 12) != 0; desc += 20)
            {
                string name = Marshal.PtrToStringAnsi((IntPtr)(b + *(uint*)(desc + 12)))!;
                if (!name.Equals(dll, StringComparison.OrdinalIgnoreCase)) continue;
                uint lookupRva = *(uint*)desc != 0 ? *(uint*)desc : *(uint*)(desc + 16);
                ulong* lookup = (ulong*)(b + lookupRva);
                IntPtr* thunks = (IntPtr*)(b + *(uint*)(desc + 16));
                for (int i = 0; lookup[i] != 0; i++)
                {
                    if ((lookup[i] & 0x8000000000000000UL) != 0) continue;   // by ordinal
                    string imported = Marshal.PtrToStringAnsi((IntPtr)(b + (uint)lookup[i] + 2))!;
                    if (imported != function) continue;
                    if (!VirtualProtect((IntPtr)(thunks + i), (UIntPtr)sizeof(IntPtr), PAGE_READWRITE, out uint old)) return;
                    thunks[i] = replacement;
                    VirtualProtect((IntPtr)(thunks + i), (UIntPtr)sizeof(IntPtr), old, out _);
                    return;
                }
            }
        }

        private const string ClassName = "WpfWebGpuWglShim";
        private const uint CS_OWNDC = 0x20, WS_POPUP = 0x80000000, PAGE_READWRITE = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        private struct WndClass
        {
            public uint style;
            public IntPtr proc;
            public int clsExtra, wndExtra;
            public IntPtr inst, icon, cursor, background, menu;
            public char* name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Pfd
        {
            public ushort size, version;
            public uint flags;
            public byte pixelType, colorBits, r1, r2, r3, r4, r5, r6, alphaBits, alphaShift, accumBits, a1, a2, a3, a4, depthBits, stencilBits, auxBuffers, layerType, reserved;
            public uint layerMask, visibleMask, damageMask;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
        [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
        [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr inst);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc, Pfd* pfd);
        [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc, int format, Pfd* pfd);
        [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr rc);
    }
}
