// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A transform of the application's, registered with MFTRegisterLocal (MFLocalTransformProbe): an IMFTransform that logs
// each call made of it and passes it to a real transform inside (the system's encoder or decoder), and the IClassFactory
// that makes it -- to see whether a writer or a reader takes the application's transform, and what it asks of it.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal sealed unsafe class SpyTransform : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private static readonly Guid SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public readonly IntPtr Inner;
        private readonly SpyFactory _factory;
        private readonly int _n;

        public SpyTransform(SpyFactory factory, IntPtr inner, int n) : base(s_vt) { _factory = factory; Inner = inner; _n = n; }
        protected override void OnFinalRelease() { Log("released"); MFReader.Release(Inner); }

        private void Log(string c) { lock (_factory.Calls) _factory.Calls.Add($"[{_n}] {c}"); }
        private static SpyTransform S(IntPtr self) => From<SpyTransform>(self)!;
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static string Hr(int hr) => hr == 0 ? "ok" : $"0x{hr:x8}";
        private static string T(IntPtr t) => t == IntPtr.Zero ? "null" : MFReader.Name(MFReader.G(t, SUB));

        private static Vtable Build()
        {
            var vt = new Vtable("IMFTransform", 26, new Guid("bf94c121-5b05-4e6f-8000-ba598961414d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)&GetStreamLimits);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetStreamCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)&GetStreamIDs);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetInputStreamInfo);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetOutputStreamInfo);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetAttributes);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputStreamAttributes);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputStreamAttributes);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, int>)&DeleteInputStream);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, uint*, int>)&AddInputStreams);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetInputAvailableType);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetOutputAvailableType);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetInputType);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetOutputType);
            vt.Set(17, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputCurrentType);
            vt.Set(18, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputCurrentType);
            vt.Set(19, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStatus);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint*, int>)&GetOutputStatus);
            vt.Set(21, (delegate* unmanaged<IntPtr, long, long, int>)&SetOutputBounds);
            vt.Set(22, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&ProcessEvent);
            vt.Set(23, (delegate* unmanaged<IntPtr, uint, nuint, int>)&ProcessMessage);
            vt.Set(24, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&ProcessInput);
            vt.Set(25, (delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)&ProcessOutput);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetStreamLimits(IntPtr self, uint* a, uint* b, uint* c, uint* d) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)Fn(x.Inner, 3))(x.Inner, a, b, c, d); x.Log($"GetStreamLimits {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetStreamCount(IntPtr self, uint* i, uint* o) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint*, uint*, int>)Fn(x.Inner, 4))(x.Inner, i, o); x.Log($"GetStreamCount {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetStreamIDs(IntPtr self, uint ni, uint* i, uint no, uint* o) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)Fn(x.Inner, 5))(x.Inner, ni, i, no, o); x.Log($"GetStreamIDs {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetInputStreamInfo(IntPtr self, uint id, byte* info) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(x.Inner, 6))(x.Inner, id, info); x.Log($"GetInputStreamInfo {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetOutputStreamInfo(IntPtr self, uint id, uint* info) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(x.Inner, 7))(x.Inner, id, info); x.Log($"GetOutputStreamInfo {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetAttributes(IntPtr self, IntPtr* a) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(x.Inner, 8))(x.Inner, a); x.Log($"GetAttributes {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetInputStreamAttributes(IntPtr self, uint id, IntPtr* a) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(x.Inner, 9))(x.Inner, id, a); x.Log($"GetInputStreamAttributes {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetOutputStreamAttributes(IntPtr self, uint id, IntPtr* a) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(x.Inner, 10))(x.Inner, id, a); x.Log($"GetOutputStreamAttributes {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int DeleteInputStream(IntPtr self, uint id) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, int>)Fn(x.Inner, 11))(x.Inner, id); x.Log($"DeleteInputStream {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int AddInputStreams(IntPtr self, uint n, uint* ids) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(x.Inner, 12))(x.Inner, n, ids); x.Log($"AddInputStreams {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetInputAvailableType(IntPtr self, uint id, uint i, IntPtr* t) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(x.Inner, 13))(x.Inner, id, i, t); x.Log($"GetInputAvailableType {i} {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetOutputAvailableType(IntPtr self, uint id, uint i, IntPtr* t) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(x.Inner, 14))(x.Inner, id, i, t); x.Log($"GetOutputAvailableType {i} {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int SetInputType(IntPtr self, uint id, IntPtr t, uint f) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(x.Inner, 15))(x.Inner, id, t, f); x.Log($"SetInputType {T(t)}{(f != 0 ? $" flags {f}" : "")} {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int SetOutputType(IntPtr self, uint id, IntPtr t, uint f) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(x.Inner, 16))(x.Inner, id, t, f); x.Log($"SetOutputType {T(t)}{(f != 0 ? $" flags {f}" : "")} {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetInputCurrentType(IntPtr self, uint id, IntPtr* t) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(x.Inner, 17))(x.Inner, id, t); x.Log($"GetInputCurrentType {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetOutputCurrentType(IntPtr self, uint id, IntPtr* t) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(x.Inner, 18))(x.Inner, id, t); x.Log($"GetOutputCurrentType {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetInputStatus(IntPtr self, uint id, uint* f) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(x.Inner, 19))(x.Inner, id, f); x.Log($"GetInputStatus {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int GetOutputStatus(IntPtr self, uint* f) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(x.Inner, 20))(x.Inner, f); x.Log($"GetOutputStatus {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int SetOutputBounds(IntPtr self, long a, long b) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, long, long, int>)Fn(x.Inner, 21))(x.Inner, a, b); x.Log($"SetOutputBounds {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int ProcessEvent(IntPtr self, uint id, IntPtr e) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(x.Inner, 22))(x.Inner, id, e); x.Log($"ProcessEvent {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int ProcessMessage(IntPtr self, uint m, nuint p) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(x.Inner, 23))(x.Inner, m, p); x.Log($"ProcessMessage 0x{m:x8} {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int ProcessInput(IntPtr self, uint id, IntPtr s, uint f) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(x.Inner, 24))(x.Inner, id, s, f); x.Log($"ProcessInput {Hr(hr)}"); return hr; }
        [UnmanagedCallersOnly] private static int ProcessOutput(IntPtr self, uint f, uint n, byte* b, uint* st) { var x = S(self); int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(x.Inner, 25))(x.Inner, f, n, b, st); x.Log($"ProcessOutput {Hr(hr)}"); return hr; }
    }

    /// <summary>The IClassFactory of the spy: each instance a spy over a new inner transform; every call logged.</summary>
    internal sealed unsafe class SpyFactory : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly List<string> Calls = new();
        private readonly Func<IntPtr> _inner;
        public int Made;

        public SpyFactory(Func<IntPtr> inner) : base(s_vt) { _inner = inner; }

        private static Vtable Build()
        {
            var vt = new Vtable("IClassFactory", 5, new Guid("00000001-0000-0000-C000-000000000046"));
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)&CreateInstance);
            vt.Set(4, (delegate* unmanaged<IntPtr, int, int>)&LockServer);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int CreateInstance(IntPtr self, IntPtr outer, Guid* riid, IntPtr* ppv)
        {
            var f = From<SpyFactory>(self)!;
            *ppv = IntPtr.Zero;
            string iid = *riid == new Guid("bf94c121-5b05-4e6f-8000-ba598961414d") ? "IMFTransform" : riid->ToString();
            if (outer != IntPtr.Zero) { lock (f.Calls) f.Calls.Add($"CreateInstance {iid} aggregated: CLASS_E_NOAGGREGATION"); return unchecked((int)0x80040110); }
            IntPtr inner = f._inner();
            if (inner == IntPtr.Zero) { lock (f.Calls) f.Calls.Add($"CreateInstance {iid}: no inner transform"); return unchecked((int)0x80004005); }
            var spy = new SpyTransform(f, inner, ++f.Made);
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(spy.Pointer, 0))(spy.Pointer, riid, ppv);
            spy.Release();
            lock (f.Calls) f.Calls.Add($"CreateInstance {iid}: [{f.Made}] 0x{hr:x8}");
            return hr;
        }

        [UnmanagedCallersOnly] private static int LockServer(IntPtr self, int l) { var f = From<SpyFactory>(self)!; lock (f.Calls) f.Calls.Add($"LockServer {l}"); return 0; }
    }
}
