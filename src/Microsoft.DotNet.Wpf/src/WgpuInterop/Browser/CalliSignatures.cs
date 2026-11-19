// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The signatures of the browser flavor's calls through native function pointers (IL calli), as P/Invokes that are
// never called and never bound: Mono on WebAssembly makes an interp-to-native trampoline for each signature a P/Invoke
// has and for none a calli has, and stops at a calli of no trampoline. Written by CalliSignatureTests (MF_WRITE_CALLI=1),
// which checks that it holds every signature of the built assembly: the letters are Mono's (I 32-bit and pointers, L
// 64-bit, F, D, V void), the return's first.
//

#if WGPU_BROWSER
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Browser
{
    internal static class CalliSignatures
    {
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_II(int a0);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_III(int a0, int a1);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIII(int a0, int a1, int a2);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIII(int a0, int a1, int a2, int a3);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIII(int a0, int a1, int a2, int a3, int a4);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIIII(int a0, int a1, int a2, int a3, int a4, int a5);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIIIIIIII(int a0, int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIIIILI(int a0, int a1, int a2, int a3, long a4, int a5);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IIL(int a0, long a1);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern int Sig_IILII(int a0, long a1, int a2, int a3);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern void Sig_VII(int a0, int a1);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern void Sig_VIII(int a0, int a1, int a2);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern void Sig_VIIIIIII(int a0, int a1, int a2, int a3, int a4, int a5, int a6);
        [DllImport("wpfwebgpu-calli-signatures")] internal static extern void Sig_VIIIIIIIII(int a0, int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8);
    }
}
#endif
