// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The signatures of the browser flavor's calls through native function pointers (IL calli), as P/Invokes that are
// never called and never bound: Mono on WebAssembly makes an interp-to-native trampoline for each signature a P/Invoke
// has and for none a calli has, and stops at a calli of no trampoline. Written by CalliSignatureTests (MF_WRITE_CALLI=1),
// which checks that it holds every signature of the built assembly: the letters are Mono's (I 32-bit and pointers, L
// 64-bit, F, D, V void), the return's first.
//

using System.Runtime.InteropServices;

namespace WgpuInterop.WasmMedia
{
    internal static class CalliSignatures
    {
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIIIII(int a0, int a1, int a2, int a3, int a4, int a5, int a6);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIILL(int a0, int a1, int a2, int a3, long a4, long a5);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIL(int a0, int a1, long a2);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIILI(int a0, int a1, long a2, int a3);
    }
}
