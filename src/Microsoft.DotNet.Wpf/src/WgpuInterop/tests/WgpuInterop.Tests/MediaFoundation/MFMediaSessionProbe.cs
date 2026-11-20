// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What Media Foundation's media session does (MF_PROBE_SESSION=<file> writes the report): a partial topology of a
// file's streams to sample grabbers (video as RGB32, audio as PCM: the topology loader adds the decoders and
// converters), its events from SetTopology to the end, the full topology it resolved (each node's type, object and
// attributes, the types between them), the grabbers' calls; Start, Pause, a seek, Stop and their events and the
// clock's times; Close and Shutdown. Then the audio renderer (SAR) and the video renderer (EVR) as the outputs.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    /// <summary>IMFSampleGrabberSinkCallback: each call, as a line.</summary>
    internal sealed unsafe class GrabberCallback : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly ConcurrentQueue<string> Calls = new();
        public int Samples;
        public GrabberCallback() : base(s_vt) { }
        private static Vtable Build()
        {
            var vt = new Vtable("IMFSampleGrabberSinkCallback", 11, new Guid("8c7b80bf-ee42-4b59-b1df-55668e1bdca8"), new Guid("f6696e82-74f7-4f3d-a178-8a5e09c3659f"));
            vt.Set(3, (delegate* unmanaged<IntPtr, long, long, int>)&OnClockStart);
            vt.Set(4, (delegate* unmanaged<IntPtr, long, int>)&OnClockStop);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, int>)&OnClockPause);
            vt.Set(6, (delegate* unmanaged<IntPtr, long, int>)&OnClockRestart);
            vt.Set(7, (delegate* unmanaged<IntPtr, long, float, int>)&OnClockSetRate);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr, int>)&OnSetPresentationClock);
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, uint, long, long, byte*, uint, int>)&OnProcessSample);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&OnShutdown);
            return vt;
        }
        private static GrabberCallback G(IntPtr self) => From<GrabberCallback>(self)!;
        [UnmanagedCallersOnly] private static int OnClockStart(IntPtr self, long sys, long offset) { G(self).Calls.Enqueue($"clock start offset {offset}"); return 0; }
        [UnmanagedCallersOnly] private static int OnClockStop(IntPtr self, long sys) { G(self).Calls.Enqueue("clock stop"); return 0; }
        [UnmanagedCallersOnly] private static int OnClockPause(IntPtr self, long sys) { G(self).Calls.Enqueue("clock pause"); return 0; }
        [UnmanagedCallersOnly] private static int OnClockRestart(IntPtr self, long sys) { G(self).Calls.Enqueue("clock restart"); return 0; }
        [UnmanagedCallersOnly] private static int OnClockSetRate(IntPtr self, long sys, float rate) { G(self).Calls.Enqueue($"clock rate {rate}"); return 0; }
        [UnmanagedCallersOnly] private static int OnSetPresentationClock(IntPtr self, IntPtr clock) { G(self).Calls.Enqueue($"set clock {(clock == IntPtr.Zero ? "null" : "clock")}"); return 0; }
        [UnmanagedCallersOnly]
        private static int OnProcessSample(IntPtr self, Guid* major, uint flags, long time, long duration, byte* buffer, uint size)
        {
            var g = G(self);
            int n = Interlocked.Increment(ref g.Samples);
            if (n <= 3) g.Calls.Enqueue($"sample {MFReader.Name(*major)} flags {flags} time {time} duration {duration} size {size}");
            return 0;
        }
        [UnmanagedCallersOnly] private static int OnShutdown(IntPtr self) { G(self).Calls.Enqueue("shutdown"); return 0; }
    }

    public sealed unsafe class MFMediaSessionProbe
    {
        [DllImport("mf.dll", EntryPoint = "MFCreateMediaSession")] private static extern int WinSession(IntPtr config, IntPtr* session);
        [DllImport("mf.dll", EntryPoint = "MFCreateTopology")] private static extern int WinTopology(IntPtr* topology);
        [DllImport("mf.dll", EntryPoint = "MFCreateTopologyNode")] private static extern int WinNode(uint type, IntPtr* node);
        [DllImport("mf.dll", EntryPoint = "MFCreateSampleGrabberSinkActivate")] private static extern int WinGrabber(IntPtr type, IntPtr callback, IntPtr* activate);
        [DllImport("mf.dll", EntryPoint = "MFCreateAudioRendererActivate")] private static extern int WinSar(IntPtr* activate);
        [DllImport("mf.dll", EntryPoint = "MFCreateVideoRendererActivate")] private static extern int WinEvr(IntPtr hwnd, IntPtr* activate);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern int DestroyWindow(IntPtr hwnd);

        private static bool Win => OperatingSystem.IsWindows();
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static void Release(IntPtr o) => MFReader.Release(o);

        internal static int Session(out IntPtr s) { IntPtr p; int hr = Win ? WinSession(IntPtr.Zero, &p) : MFExports.MFCreateMediaSession(IntPtr.Zero, &p); s = hr >= 0 ? p : IntPtr.Zero; return hr; }
        internal static IntPtr Topology() { IntPtr p; int hr = Win ? WinTopology(&p) : MFExports.MFCreateTopology(&p); Assert.True(hr >= 0, $"MFCreateTopology {Hr(hr)}"); return p; }
        internal static IntPtr Node(uint type) { IntPtr p; int hr = Win ? WinNode(type, &p) : MFExports.MFCreateTopologyNode(type, &p); Assert.True(hr >= 0, $"MFCreateTopologyNode {Hr(hr)}"); return p; }
        internal static int Grabber(IntPtr type, IntPtr callback, out IntPtr a) { IntPtr p; int hr = Win ? WinGrabber(type, callback, &p) : MFExports.MFCreateSampleGrabberSinkActivate(type, callback, &p); a = hr >= 0 ? p : IntPtr.Zero; return hr; }
        internal static int Sar(out IntPtr a) { IntPtr p; int hr = Win ? WinSar(&p) : MFExports.MFCreateAudioRendererActivate(&p); a = hr >= 0 ? p : IntPtr.Zero; return hr; }
        internal static int Evr(IntPtr hwnd, out IntPtr a) { IntPtr p; int hr = Win ? WinEvr(hwnd, &p) : MFExports.MFCreateVideoRendererActivate(hwnd, &p); a = hr >= 0 ? p : IntPtr.Zero; return hr; }

        private static readonly Guid MF_TOPONODE_SOURCE = new("835c58ec-e075-4bc7-bcba-4de000df9ae6"), MF_TOPONODE_PRESENTATION_DESCRIPTOR = new("835c58ed-e075-4bc7-bcba-4de000df9ae6");
        private static readonly Guid MF_TOPONODE_STREAM_DESCRIPTOR = new("835c58ee-e075-4bc7-bcba-4de000df9ae6");
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid RGB32 = new("00000016-0000-0010-8000-00aa00389b71"), Float = new("00000003-0000-0010-8000-00aa00389b71");

        internal static readonly Dictionary<uint, string> Events = new()
        {
            [1] = "MEError", [101] = "MESessionTopologySet", [102] = "MESessionTopologiesCleared", [103] = "MESessionStarted", [104] = "MESessionPaused",
            [105] = "MESessionStopped", [106] = "MESessionClosed", [107] = "MESessionEnded", [108] = "MESessionRateChanged", [109] = "MESessionScrubSampleComplete",
            [110] = "MESessionCapabilitiesChanged", [111] = "MESessionTopologyStatus", [112] = "MESessionNotifyPresentationTime", [113] = "MENewPresentation",
            [114] = "MELicenseAcquisitionStart", [115] = "MELicenseAcquisitionCompleted", [116] = "MEIndividualizationStart", [117] = "MEIndividualizationCompleted",
            [118] = "MEEnablerProgress", [119] = "MEEnablerCompleted", [120] = "MEPolicyError", [121] = "MEPolicyReport", [122] = "MEBufferingStarted",
            [123] = "MEBufferingStopped", [124] = "MEConnectStart", [125] = "MEConnectEnd", [126] = "MEReconnectStart", [127] = "MEReconnectEnd",
            [128] = "MERendererEvent", [129] = "MESessionStreamSinkFormatChanged", [211] = "MEEndOfPresentation", [3] = "MEExtendedType",
        };

        /// <summary>The session's events, of the time given for the first and up to the one wanted (then briefly for the rest).</summary>
        private static List<string> Drain(IntPtr session, int firstMs, uint until, List<uint>? seen = null)
        {
            var log = new List<string>();
            var sw = Stopwatch.StartNew();
            bool got = false;
            while (sw.ElapsedMilliseconds < (got ? firstMs + 300 : firstMs))
            {
                IntPtr e;
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(session, 3))(session, 1, &e);
                if (hr < 0) { if (hr != unchecked((int)0xC00D3E80)) { log.Add($"  get event {Hr(hr)}"); break; } Thread.Sleep(5); continue; }
                uint type; int status;
                ((delegate* unmanaged<IntPtr, uint*, int>)Fn(e, 33))(e, &type);
                ((delegate* unmanaged<IntPtr, int*, int>)Fn(e, 35))(e, &status);
                var pv = stackalloc byte[24];
                new Span<byte>(pv, 24).Clear();
                ((delegate* unmanaged<IntPtr, byte*, int>)Fn(e, 36))(e, pv);
                ushort vt = *(ushort*)pv;
                Guid ext; ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(e, 34))(e, &ext);
                string v = vt switch { 0 => "", 13 => " UNKNOWN", 20 => " I8", 21 => " UI8", 19 => $" UI4 {*(uint*)(pv + 8)}", 31 => $" LPWSTR \"{Marshal.PtrToStringUni(*(IntPtr*)(pv + 8))}\"", _ => $" VT {vt}" };
                if (ext != Guid.Empty) v += $" ext {ext}";
                if (vt == 31) Marshal.FreeCoTaskMem(*(IntPtr*)(pv + 8));
                if (vt == 13) Release(*(IntPtr*)(pv + 8));
                var attrs = MFReader.Dump(e);
                log.Add($"  event {(Events.TryGetValue(type, out var n) ? n : type.ToString())} {Hr(status)}{v}{(attrs.Count > 0 ? " [" + string.Join("; ", attrs) + "]" : "")}");
                seen?.Add(type);
                Release(e);
                if (type == until && !got) { got = true; firstMs = (int)sw.ElapsedMilliseconds; }
            }
            return log;
        }

        private static int Start(IntPtr session, long? at)
        {
            var pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            if (at is long a) { *(ushort*)pv = 20; *(long*)(pv + 8) = a; }
            Guid g = Guid.Empty;
            return ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Fn(session, 9))(session, &g, pv);
        }

        /// <summary>A partial topology: each stream of the source to the output its kind is given.</summary>
        private static IntPtr Partial(IntPtr source, IntPtr pd, Func<Guid, IntPtr> output, List<string> log)
        {
            IntPtr topo = Topology();
            uint n; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(pd, 33))(pd, &n);
            for (uint i = 0; i < n; i++)
            {
                int sel; IntPtr sd;
                ((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Fn(pd, 34))(pd, i, &sel, &sd);
                IntPtr h; ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(sd, 34))(sd, &h);
                Guid major; ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(h, 8))(h, &major);
                Release(h);
                IntPtr act = output(major);
                if (act == IntPtr.Zero) { ((delegate* unmanaged<IntPtr, uint, int>)Fn(pd, 36))(pd, i); Release(sd); continue; }
                IntPtr src = Node(1), dst = Node(0);
                SetUnknown(src, MF_TOPONODE_SOURCE, source);
                SetUnknown(src, MF_TOPONODE_PRESENTATION_DESCRIPTOR, pd);
                SetUnknown(src, MF_TOPONODE_STREAM_DESCRIPTOR, sd);
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(dst, 33))(dst, act);                    // SetObject
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(topo, 34))(topo, src);                  // AddNode
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(topo, 34))(topo, dst);
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(src, 40))(src, 0, dst, 0);   // ConnectOutput
                log.Add($"  stream {i} {MFReader.Name(major)} connected {Hr(hr)}");
                log.AddRange(MFReader.Dump(src).Select(l => "    source node " + l));
                Release(src); Release(dst); Release(act); Release(sd);
            }
            return topo;
        }

        private static void SetUnknown(IntPtr a, Guid k, IntPtr v) => ((delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)Fn(a, 27))(a, &k, v);

        /// <summary>The full topology: each node's type, its object's kind and its attributes, its connections and the types between.</summary>
        private static List<string> Full(IntPtr session)
        {
            var log = new List<string>();
            IntPtr t;
            int hr = ((delegate* unmanaged<IntPtr, uint, ulong, IntPtr*, int>)Fn(session, 16))(session, 1, 0, &t);       // MFSESSION_GETFULLTOPOLOGY_CURRENT
            log.Add($"  full topology {Hr(hr)}");
            if (hr < 0) return log;
            log.AddRange(MFReader.Dump(t).Select(l => "    topo " + l));
            ushort n; ((delegate* unmanaged<IntPtr, ushort*, int>)Fn(t, 36))(t, &n);
            log.Add($"    nodes {n}");
            var ids = new Dictionary<ulong, int>();
            var nodes = new List<IntPtr>();
            for (ushort i = 0; i < n; i++)
            {
                IntPtr node; ((delegate* unmanaged<IntPtr, ushort, IntPtr*, int>)Fn(t, 37))(t, i, &node);
                ulong id; ((delegate* unmanaged<IntPtr, ulong*, int>)Fn(node, 36))(node, &id);
                ids[id] = i; nodes.Add(node);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                IntPtr node = nodes[i];
                uint type; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(node, 35))(node, &type);
                IntPtr obj; int oh = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(node, 34))(node, &obj);
                string what = oh < 0 ? "none" : Kind(obj);
                uint ins, outs;
                ((delegate* unmanaged<IntPtr, uint*, int>)Fn(node, 38))(node, &ins);
                ((delegate* unmanaged<IntPtr, uint*, int>)Fn(node, 39))(node, &outs);
                log.Add($"    node {i}: type {type} object {what} inputs {ins} outputs {outs}");
                log.AddRange(MFReader.Dump(node).Select(l => "      " + l));
                for (uint o = 0; o < outs; o++)
                {
                    IntPtr down; uint di;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, int>)Fn(node, 43))(node, o, &down, &di) < 0) continue;
                    ulong did; ((delegate* unmanaged<IntPtr, ulong*, int>)Fn(down, 36))(down, &did);
                    log.Add($"      output {o} -> node {(ids.TryGetValue(did, out int k) ? k : -1)} input {di}");
                    IntPtr pt;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(node, 45))(node, o, &pt) >= 0 && pt != IntPtr.Zero)
                    { log.Add($"        pref type {MFReader.Name(MFReader.G(pt, MAJOR))} {MFReader.Name(MFReader.G(pt, SUB))}"); Release(pt); }
                    Release(down);
                }
                // a transform's types now
                if (oh >= 0 && MFMediaSourceProbe.Qi(obj, new Guid("bf94c121-5b05-4e6f-8000-ba598961414d")) is var mft && mft != IntPtr.Zero)
                {
                    IntPtr it, ot;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(mft, 17))(mft, 0, &it) >= 0) { log.Add($"      in {MFReader.Name(MFReader.G(it, SUB))}"); Release(it); }
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(mft, 18))(mft, 0, &ot) >= 0) { log.Add($"      out {MFReader.Name(MFReader.G(ot, SUB))}"); Release(ot); }
                    Release(mft);
                }
                if (oh >= 0) Release(obj);
            }
            foreach (var node in nodes) Release(node);
            Release(t);
            return log;
        }

        /// <summary>What a node's object is: a transform, a stream sink, an activate, a source.</summary>
        private static string Kind(IntPtr o)
        {
            foreach (var (iid, name) in new[] { ("bf94c121-5b05-4e6f-8000-ba598961414d", "IMFTransform"), ("0a97b3cf-8e7c-4a3d-8f8c-0c843dc247fb", "IMFStreamSink"), ("7fee9e9a-4a89-47a6-899c-b6a53a70fb67", "IMFActivate"), ("279a808d-aec7-40c8-9c6b-a6b492c78a66", "IMFMediaSource") })
            {
                IntPtr p = MFMediaSourceProbe.Qi(o, new Guid(iid));
                if (p != IntPtr.Zero) { Release(p); return name; }
            }
            return "other";
        }

        private static IntPtr GrabberOf(Guid major, Dictionary<Guid, GrabberCallback> callbacks)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, major);
            MFReader.SetGuid(t, SUB, major == Video ? RGB32 : Float);
            var cb = new GrabberCallback();
            callbacks[major] = cb;
            Grabber(t, cb.Pointer, out var a);
            Release(t);
            return a;
        }

        private static string Times(IntPtr clock)
        {
            long t; int hr = ((delegate* unmanaged<IntPtr, long*, int>)Fn(clock, 10))(clock, &t);
            uint st; ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(clock, 6))(clock, 0, &st);
            return $"clock {Hr(hr)} time {t / 10_000} ms state {st}";
        }

        /// <summary>A file to sample grabbers: the events, the full topology, the grabbers' calls, the transport.</summary>
        internal static List<string> Grabbers(string path)
        {
            var log = new List<string> { "==== sample grabbers" };
            MFMediaSourceProbe.CreateResolver(out var resolver);
            int hr = MFMediaSourceProbe.FromUrl(resolver, path, 1, out _, out var source);
            log.Add($"source {Hr(hr)}");
            IntPtr pd = MFMediaSourceProbe.Pd(source, out _);
            log.Add($"session {Hr(Session(out var session))}");
            var callbacks = new Dictionary<Guid, GrabberCallback>();
            IntPtr topo = Partial(source, pd, m => GrabberOf(m, callbacks), log);
            log.Add($"set topology {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(session, 7))(session, 0, topo))}");
            log.AddRange(Drain(session, 3000, 111));
            uint caps; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(session, 15))(session, &caps);
            log.Add($"capabilities 0x{caps:x}");
            log.Add($"start {Hr(Start(session, null))}");
            log.AddRange(Drain(session, 5000, 103));
            IntPtr clockU; hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(session, 14))(session, &clockU);
            IntPtr clock = hr >= 0 ? MFMediaSourceProbe.Qi(clockU, new Guid("868ce85c-8ea9-4f55-ab82-b009a910a805")) : IntPtr.Zero;
            if (hr >= 0) Release(clockU);
            log.Add($"get clock {Hr(hr)} presentation clock {clock != IntPtr.Zero}");
            log.AddRange(Full(session));
            Thread.Sleep(300);
            if (clock != IntPtr.Zero) log.Add("  playing: " + Times(clock));
            log.Add($"pause {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 10))(session))}");
            log.AddRange(Drain(session, 2000, 104));
            if (clock != IntPtr.Zero) log.Add("  paused: " + Times(clock));
            log.Add($"start at 400 ms {Hr(Start(session, 4_000_000))}");
            log.AddRange(Drain(session, 3000, 103));
            if (clock != IntPtr.Zero) log.Add("  seeked: " + Times(clock));
            log.Add("to the end:");
            log.AddRange(Drain(session, 10_000, 107));
            if (clock != IntPtr.Zero) log.Add("  ended: " + Times(clock));
            log.Add($"start again {Hr(Start(session, 0))}");
            log.AddRange(Drain(session, 3000, 103));
            log.Add($"stop {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 11))(session))}");
            log.AddRange(Drain(session, 2000, 105));
            if (clock != IntPtr.Zero) log.Add("  stopped: " + Times(clock));
            log.Add($"close {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 12))(session))}");
            log.AddRange(Drain(session, 3000, 106));
            foreach (var (major, cb) in callbacks)
            {
                log.Add($"grabber {MFReader.Name(major)}: samples {(cb.Samples > 0 ? "some" : "none")}");
                foreach (var c in cb.Calls) log.Add("  " + c);
            }
            log.Add($"shutdown {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 13))(session))}");
            log.Add($"start after shutdown {Hr(Start(session, null))}");
            ((delegate* unmanaged<IntPtr, int>)Fn(source, 12))(source);
            if (clock != IntPtr.Zero) Release(clock);
            Release(topo); Release(pd); Release(source); Release(session); Release(resolver);
            return log;
        }

        /// <summary>The system's renderers as the outputs: the audio renderer, the video renderer of a window.</summary>
        internal static List<string> Renderers(string path, Guid only)
        {
            var log = new List<string> { $"==== renderer of {MFReader.Name(only)}" };
            IntPtr hwnd = Win ? CreateWindowExW(0, "STATIC", "probe", 0x10CF0000, 0, 0, 320, 240, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;
            MFMediaSourceProbe.CreateResolver(out var resolver);
            MFMediaSourceProbe.FromUrl(resolver, path, 1, out _, out var source);
            IntPtr pd = MFMediaSourceProbe.Pd(source, out _);
            Session(out var session);
            IntPtr topo = Partial(source, pd, m =>
            {
                if (m != only) return IntPtr.Zero;
                int hr = m == Video ? Evr(hwnd, out var a) : Sar(out a);
                log.Add($"  renderer of {MFReader.Name(m)} {Hr(hr)}");
                if (a != IntPtr.Zero) log.AddRange(MFReader.Dump(a).Select(l => "    " + l));
                return a;
            }, log);
            log.Add($"set topology {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(session, 7))(session, 0, topo))}");
            log.AddRange(Drain(session, 4000, 111));
            log.Add($"start {Hr(Start(session, null))}");
            log.AddRange(Drain(session, 5000, 103));
            log.AddRange(Full(session));
            if (MFMediaSourceProbe.Qi(session, new Guid("fa993888-4383-415a-a930-dd472a8cf6f7")) is var gs && gs != IntPtr.Zero)
            {
                // IMFGetService: the video renderer's display control (MR_VIDEO_RENDER_SERVICE), its sizes
                IntPtr dc; var svc = new Guid("1092a86c-ab1a-459a-a336-831fbc4d11ff"); var iid = new Guid("a490b1e4-ab84-4d31-a1b2-181e03b1077a");
                int ghr = ((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Fn(gs, 3))(gs, &svc, &iid, &dc);
                log.Add($"  display control {Hr(ghr)}");
                if (ghr >= 0)
                {
                    long* sz = stackalloc long[2];
                    int nh = ((delegate* unmanaged<IntPtr, long*, long*, int>)Fn(dc, 3))(dc, sz, sz + 1);       // GetNativeVideoSize(SIZE*, SIZE*)
                    log.Add($"    native size {Hr(nh)} {(int)sz[0]}x{(int)(sz[0] >> 32)} aspect {(int)sz[1]}x{(int)(sz[1] >> 32)}");
                    IntPtr w; int wh = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(dc, 10))(dc, &w);          // GetVideoWindow
                    log.Add($"    window {Hr(wh)} {(w == hwnd && w != IntPtr.Zero ? "the app's" : w == IntPtr.Zero ? "none" : "another")}");
                    uint mode; int mh = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(dc, 8))(dc, &mode);          // GetAspectRatioMode
                    log.Add($"    aspect mode {Hr(mh)} {mode}");
                    Release(dc);
                }
                Release(gs);
            }
            log.Add("to the end:");
            log.AddRange(Drain(session, 10_000, 107));
            log.Add($"close {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 12))(session))}");
            log.AddRange(Drain(session, 3000, 106));
            log.Add($"shutdown {Hr(((delegate* unmanaged<IntPtr, int>)Fn(session, 13))(session))}");
            ((delegate* unmanaged<IntPtr, int>)Fn(source, 12))(source);
            Release(topo); Release(pd); Release(source); Release(session); Release(resolver);
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            return log;
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            string path = new Mp4TestFile { Frames = 25, AudioFrames = 43 }.WriteTemp("session");
            var log = Grabbers(path);
            log.AddRange(Renderers(path, Video));
            log.AddRange(Renderers(path, Audio));
            return log;
        }

        /// <summary>The report but its times (the clock's, the events' of occurrence), the window's handle and the audio device's id.</summary>
        internal static List<string> Normalize(IEnumerable<string> report)
        {
            var o = new List<string>();
            foreach (var raw in report)
            {
                string l = raw;
                int t = l.IndexOf(" time ");
                if (l.Contains(": clock ") && t > 0) { int ms = l.IndexOf(" ms", t); if (ms > 0) l = l[..(t + 6)] + "#" + l[ms..]; }
                foreach (var k in new[] { "190e852f-6238-42d1-b5af-69ea338ef850 = UI8 ", "9a2dbbdd-f57e-4162-82b9-6831377682d3 = UI8 " })
                {
                    int at = l.IndexOf(k);
                    if (at < 0) continue;
                    int end = l.IndexOf(')', at);
                    if (end > 0) l = l[..(at + k.Length)] + "#" + l[(end + 1)..];
                }
                int q = l.IndexOf(" LPWSTR \"");
                if (q > 0) { int e = l.IndexOf('"', q + 9); if (e > 0) l = l[..(q + 8)] + "<id>" + l[(e + 1)..]; }
                o.Add(l);
            }
            return o;
        }

        /// <summary>The sections of the report, by their "====" lines.</summary>
        internal static Dictionary<string, List<string>> Sections(IEnumerable<string> report)
        {
            var d = new Dictionary<string, List<string>>();
            List<string>? cur = null;
            foreach (var l in report)
            {
                if (l.StartsWith("==== ")) { cur = new List<string>(); d[l] = cur; }
                cur?.Add(l);
            }
            return d;
        }

        private static void Compare(string section)
        {
            var windows = Sections(System.IO.File.ReadAllLines(System.IO.Path.Combine(TestFiles.Root, "MediaFoundation", "MediaSession.windows.txt")))[section];
            MFReader.Startup();
            string path = new Mp4TestFile { Frames = 25, AudioFrames = 43 }.WriteTemp("session");
            var ours = Normalize(section == "==== sample grabbers" ? Grabbers(path) : Renderers(path, section.Contains("Video") ? Video : Audio));
            for (int i = 0; i < Math.Min(windows.Count, ours.Count); i++)
                Assert.True(windows[i] == ours[i], $"line {i}: Windows '{windows[i]}', ours '{ours[i]}'");
            Assert.Equal(windows.Count, ours.Count);
        }

        [Fact]
        public void TheSession_PlaysToSampleGrabbersAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' session is the fixture");
            Compare("==== sample grabbers");
        }

        /// <summary>Whether the system has an audio output (the audio renderer's activate makes its sink).</summary>
        private static bool HasAudioOutput()
        {
            if (Sar(out var a) < 0) return false;
            IntPtr sink; var iid = new Guid("6ef2a660-47c0-4666-b13d-cbb717f2fa2c");
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(a, 33))(a, &iid, &sink);
            if (hr >= 0) { ((delegate* unmanaged<IntPtr, int>)Fn(sink, 11))(sink); Release(sink); }
            ((delegate* unmanaged<IntPtr, int>)Fn(a, 34))(a);
            Release(a);
            return hr >= 0;
        }

        [Fact]
        public void TheSession_PlaysToTheAudioRendererAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' session is the fixture");
            Assert.SkipWhen(!HasAudioOutput(), "no audio output here");
            Compare("==== renderer of MFMediaType_Audio");
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SESSION") is not { Length: > 0 }, "MF_PROBE_SESSION=<file> writes the report (its times and ids normalized)");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SESSION")!, Normalize(Report()));
        }
    }
}
