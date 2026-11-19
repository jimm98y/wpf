// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the objects of media sources do (MFSourceObjects.cs), against Windows' (SourceObjects.windows.txt):
// stream descriptors and their type handlers, presentation descriptors, media events and event queues --
// each call's result and what it gives.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFSourceObjectsProbe
    {
        [DllImport("mfplat.dll", EntryPoint = "MFCreateEventQueue")] private static extern int WinQueue(IntPtr* q);
        [DllImport("mfplat.dll", EntryPoint = "MFStartup")] private static extern int WinStartup(uint version, uint flags);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateMediaEvent")] private static extern int WinEvent(uint type, Guid* ext, int status, byte* value, IntPtr* e);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateStreamDescriptor")] private static extern int WinSd(uint id, uint count, IntPtr* types, IntPtr* sd);
        [DllImport("mfplat.dll", EntryPoint = "MFCreatePresentationDescriptor")] private static extern int WinPd(uint count, IntPtr* sds, IntPtr* pd);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");

        private static IntPtr VideoType(Guid sub, uint w)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Video); MFReader.SetGuid(t, SUB, sub);
            MFReader.SetU64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(w, 48));
            return t;
        }

        /// <summary>A callback that records its Invoke: the event it ends, of the queue.</summary>
        private sealed class Callback : Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject
        {
            private static readonly Microsoft.Wpf.Interop.WebGpu.Direct3D.Vtable s_vt = Build();
            public IntPtr Queue;
            public readonly ManualResetEventSlim Done = new();
            public string Got = "";
            private static Microsoft.Wpf.Interop.WebGpu.Direct3D.Vtable Build()
            {
                var vt = new Microsoft.Wpf.Interop.WebGpu.Direct3D.Vtable("IMFAsyncCallback", 5, new Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"));
                vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetParameters);
                vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, int>)&Invoke);
                return vt;
            }
            public Callback() : base(s_vt) { }
            [UnmanagedCallersOnly] private static int GetParameters(IntPtr self, uint* f, uint* q) => unchecked((int)0x80004001);
            [UnmanagedCallersOnly]
            private static int Invoke(IntPtr self, IntPtr result)
            {
                var c = From<Callback>(self)!;
                IntPtr e;
                int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)MFReader.Fn(c.Queue, 5))(c.Queue, result, &e);
                uint type = 0;
                if (hr >= 0) { ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(e, 33))(e, &type); MFReader.Release(e); }
                c.Got = $"invoked: end {Hr(hr)} type {type}";
                c.Done.Set();
                return 0;
            }
        }

        private static void QueueReport(List<string> log, IntPtr t0, bool win)
        {
            byte* vbuf = stackalloc byte[24];
            IntPtr q;
            int hr = win ? WinQueue(&q) : MFExports.MFCreateEventQueue(&q);
            log.Add($"event queue {Hr(hr)}");
            IntPtr e;
            log.Add($"  get, empty, no wait {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(q, 3))(q, 1, &e))}");
            // events made with MFCreateMediaEvent and queued (Windows' QueueEventParamVar and QueueEventParamUnk fault in this
            // process on ARM64, whatever their arguments: not probed)
            var pv = stackalloc byte[24]; new Span<byte>(pv, 24).Clear(); *(ushort*)pv = 19; *(uint*)(pv + 8) = 5;
            var ext = new Guid("11111111-2222-3333-4444-555555555555");
            IntPtr e1, e2;
            hr = win ? WinEvent(101, &ext, unchecked((int)0x80004005), pv, &e1) : MFExports.MFCreateMediaEvent(101, &ext, unchecked((int)0x80004005), pv, &e1);
            log.Add($"  event var {Hr(hr)}, queued {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(q, 6))(q, e1))}");
            MFReader.Release(e1);
            new Span<byte>(pv, 24).Clear(); *(ushort*)pv = 13; *(IntPtr*)(pv + 8) = t0;
            hr = win ? WinEvent(102, null, 0, pv, &e2) : MFExports.MFCreateMediaEvent(102, null, 0, pv, &e2);
            log.Add($"  event unk {Hr(hr)}, queued {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(q, 6))(q, e2))}");
            MFReader.Release(e2);
            for (int k = 0; k < 2; k++)
            {
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(q, 3))(q, 1, &e);
                uint type; Guid g; int st;
                ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(e, 33))(e, &type);
                ((delegate* unmanaged<IntPtr, Guid*, int>)MFReader.Fn(e, 34))(e, &g);
                ((delegate* unmanaged<IntPtr, int*, int>)MFReader.Fn(e, 35))(e, &st);
                byte* v = vbuf; new Span<byte>(v, 24).Clear();
                ((delegate* unmanaged<IntPtr, byte*, int>)MFReader.Fn(e, 36))(e, v);
                ushort vt = *(ushort*)v;
                string val = vt == 19 ? $"UI4 {*(uint*)(v + 8)}" : vt == 13 ? $"UNKNOWN the type {*(IntPtr*)(v + 8) == t0}" : $"VT {vt}";
                if (vt == 13) MFReader.Release(*(IntPtr*)(v + 8));
                log.Add($"  get {Hr(hr)}: type {type} extended {g} status {Hr(st)} value {val}");
                MFReader.Release(e);
            }
            var cb = new Callback { Queue = q };
            log.Add($"  begin {Hr(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)MFReader.Fn(q, 4))(q, cb.Pointer, IntPtr.Zero))}");
            log.Add($"  begin again {Hr(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)MFReader.Fn(q, 4))(q, cb.Pointer, IntPtr.Zero))}");
            log.Add($"  get while begun {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(q, 3))(q, 1, &e))}");
            IntPtr me;
            hr = win ? WinEvent(103, null, 0, null, &me) : MFExports.MFCreateMediaEvent(103, null, 0, null, &me);
            log.Add($"  media event {Hr(hr)}; queue it {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(q, 6))(q, me))}");
            MFReader.Release(me);
            log.Add($"  {(cb.Done.Wait(5000) ? cb.Got : "not invoked")}");
            log.Add($"  shutdown {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(q, 9))(q))}");
            log.Add($"  get after {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(q, 3))(q, 1, &e))}");
            IntPtr e3; hr = win ? WinEvent(104, null, 0, null, &e3) : MFExports.MFCreateMediaEvent(104, null, 0, null, &e3);
            log.Add($"  queue after {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(q, 6))(q, e3))}");
            MFReader.Release(e3);
            MFReader.Release(q); cb.Release();
        }

        internal static List<string> Report()
        {
            var log = new List<string>();
            MFReader.Startup();
            bool win = OperatingSystem.IsWindows();
            var nv12 = new Guid("3231564e-0000-0010-8000-00aa00389b71"); var yuy2 = new Guid("32595559-0000-0010-8000-00aa00389b71");
            IntPtr t0 = VideoType(nv12, 64), t1 = VideoType(yuy2, 64);
            QueueReport(log, t0, win);
            var types = stackalloc IntPtr[2]; types[0] = t0; types[1] = t1;
            IntPtr sd;
            int hr = win ? WinSd(7, 2, types, &sd) : MFExports.MFCreateStreamDescriptor(7, 2, types, &sd);
            log.Add($"stream descriptor {Hr(hr)}");
            uint id; ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(sd, 33))(sd, &id);
            IntPtr h; ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(sd, 34))(sd, &h);
            log.Add($"  id {id}");
            uint n; ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(h, 4))(h, &n);
            log.Add($"  types {n}");
            IntPtr got; hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(h, 5))(h, 0, &got);
            log.Add($"  type 0 {Hr(hr)} the one given {got == t0}"); MFReader.Release(got);
            hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(h, 5))(h, 5, &got);
            log.Add($"  type 5 {Hr(hr)}");
            IntPtr cur; hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(h, 7))(h, &cur);
            log.Add($"  current before {Hr(hr)}");
            Guid major; hr = ((delegate* unmanaged<IntPtr, Guid*, int>)MFReader.Fn(h, 8))(h, &major);
            log.Add($"  major before {Hr(hr)} {MFReader.Name(major)}");
            IntPtr same = VideoType(nv12, 64), other = VideoType(nv12, 80), rgb = VideoType(new Guid("00000016-0000-0010-8000-00aa00389b71"), 64);
            { IntPtr closest = IntPtr.Zero; int r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)MFReader.Fn(h, 3))(h, same, &closest); log.Add($"  supported, the same {Hr(r)} closest {(closest == IntPtr.Zero ? "none" : closest == t0 ? "type 0" : closest == t1 ? "type 1" : "another")}"); MFReader.Release(closest); }
            { IntPtr closest = IntPtr.Zero; int r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)MFReader.Fn(h, 3))(h, other, &closest); log.Add($"  supported, other size {Hr(r)} closest {(closest == IntPtr.Zero ? "none" : closest == t0 ? "type 0" : closest == t1 ? "type 1" : "another")}"); MFReader.Release(closest); }
            { IntPtr closest = IntPtr.Zero; int r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)MFReader.Fn(h, 3))(h, rgb, &closest); log.Add($"  supported, other subtype {Hr(r)} closest {(closest == IntPtr.Zero ? "none" : closest == t0 ? "type 0" : closest == t1 ? "type 1" : "another")}"); MFReader.Release(closest); }
            log.Add($"  set current, other subtype {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(h, 6))(h, rgb))}");
            log.Add($"  set current, the same {Hr(((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(h, 6))(h, same))}");
            hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(h, 7))(h, &cur);
            log.Add($"  current after {Hr(hr)} the one set {cur == same} subtype {MFReader.Name(MFReader.G(cur, SUB))}"); MFReader.Release(cur);
            MFReader.Release(same); MFReader.Release(other); MFReader.Release(rgb); MFReader.Release(h);

            var sds = stackalloc IntPtr[1]; sds[0] = sd;
            IntPtr pd;
            hr = win ? WinPd(1, sds, &pd) : MFExports.MFCreatePresentationDescriptor(1, sds, &pd);
            log.Add($"presentation descriptor {Hr(hr)}");
            ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(pd, 33))(pd, &n);
            int sel; IntPtr psd;
            ((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)MFReader.Fn(pd, 34))(pd, 0, &sel, &psd);
            log.Add($"  streams {n} selected {sel} the one given {psd == sd}"); MFReader.Release(psd);
            log.Add($"  stream 3 {Hr(((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)MFReader.Fn(pd, 34))(pd, 3, &sel, &psd))}");
            log.Add($"  select {Hr(((delegate* unmanaged<IntPtr, uint, int>)MFReader.Fn(pd, 35))(pd, 0))}, select 3 {Hr(((delegate* unmanaged<IntPtr, uint, int>)MFReader.Fn(pd, 35))(pd, 3))}");
            IntPtr clone; ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(pd, 37))(pd, &clone);
            ((delegate* unmanaged<IntPtr, uint, int>)MFReader.Fn(pd, 36))(pd, 0);
            ((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)MFReader.Fn(clone, 34))(clone, 0, &sel, &psd);
            log.Add($"  clone: selected {sel} (the original deselected after) the same descriptor {psd == sd}"); MFReader.Release(psd);
            MFReader.Release(clone); MFReader.Release(pd);

            MFReader.Release(sd); MFReader.Release(t0); MFReader.Release(t1);
            return log;
        }

        /// <summary>The objects as Media Foundation's documentation has them (Windows' own fault, called so from this process on ARM64).</summary>
        [Fact]
        public void TheObjects_DoWhatMediaFoundationsDo()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' own objects");
            var r = Report();
            if (Environment.GetEnvironmentVariable("MF_PROBE_OBJECTS") is { Length: > 0 } outFile) File.WriteAllLines(outFile, r);
            string[] expected =
            {
                "event queue 0x00000000",
                "  get, empty, no wait 0xc00d3e80",                                     // MF_E_NO_EVENTS_AVAILABLE
                "  event var 0x00000000, queued 0x00000000",
                "  event unk 0x00000000, queued 0x00000000",
                "  get 0x00000000: type 101 extended 11111111-2222-3333-4444-555555555555 status 0x80004005 value UI4 5",
                "  get 0x00000000: type 102 extended 00000000-0000-0000-0000-000000000000 status 0x00000000 value UNKNOWN the type True",
                "  begin 0x00000000",
                "  begin again 0xc00d36d9",                                              // MF_E_MULTIPLE_BEGIN
                "  get while begun 0xc00d36d9",
                "  media event 0x00000000; queue it 0x00000000",
                "  invoked: end 0x00000000 type 103",
                "  shutdown 0x00000000",
                "  get after 0xc00d3e85",                                                 // MF_E_SHUTDOWN
                "  queue after 0xc00d3e85",
                "stream descriptor 0x00000000",
                "  id 7",
                "  types 2",
                "  type 0 0x00000000 the one given True",
                "  type 5 0xc00d36b9",                                                    // MF_E_NO_MORE_TYPES
                "  current before 0xc00d36b6",                                            // MF_E_NOT_INITIALIZED
                "  major before 0x00000000 MFMediaType_Video",
                "  supported, the same 0x00000000 closest none",
                "  supported, other size 0xc00d36b4 closest none",                         // MF_E_INVALIDMEDIATYPE
                "  supported, other subtype 0xc00d36b4 closest none",
                "  set current, other subtype 0xc00d36b4",
                "  set current, the same 0x00000000",
                "  current after 0x00000000 the one set True subtype MFVideoFormat_NV12",
                "presentation descriptor 0x00000000",
                "  streams 1 selected 0 the one given True",
                "  stream 3 0x80070057",
                "  select 0x00000000, select 3 0x80070057",
                "  clone: selected 1 (the original deselected after) the same descriptor True",
            };
            Assert.Equal(string.Join("\n", expected), string.Join("\n", r));
        }
    }
}
