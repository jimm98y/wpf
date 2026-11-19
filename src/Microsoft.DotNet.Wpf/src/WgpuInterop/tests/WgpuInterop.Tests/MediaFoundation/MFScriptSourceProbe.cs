// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A media source of the application's that plays a script (MF_PROBE_SCRIPT=<file> writes the report): one stream of
// NV12, its samples, a stream tick across a gap, a change of format (MEStreamFormatChanged, its type handler's type
// changed), the end -- and what the source reader of it (MFCreateSourceReaderFromMediaSource) makes of each: its flags,
// times, samples, the native and current types after each change; asynchronously, of NV12 out and of RGB32.
//
// Windows' reader could not be had to read this source: it takes the first sample's MEMediaSample and hands it out
// never, of the synchronous ReadSample and the asynchronous alike (the source's calls in MF_PROBE_SCRIPT's ".calls"
// file); the test holds the reader to MF_SOURCE_READER_FLAG's documentation instead.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    /// <summary>The source's calls, each also appended to MF_PROBE_SCRIPT's ".calls" file as it comes.</summary>
    internal sealed class CallLog : List<string>
    {
        public new void Add(string c)
        {
            base.Add(c);
            if (Environment.GetEnvironmentVariable("MF_PROBE_SCRIPT") is { Length: > 0 } f) File.AppendAllText(f + ".calls", $"{DateTime.Now:HH:mm:ss.fff} {c}\n");
        }
    }

    internal sealed unsafe class ScriptSource : ComObject
    {
        [DllImport("mfplat.dll", EntryPoint = "MFCreateEventQueue")] private static extern int WinQueue(IntPtr* q);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateStreamDescriptor")] private static extern int WinSd(uint id, uint n, IntPtr* types, IntPtr* sd);
        [DllImport("mfplat.dll", EntryPoint = "MFCreatePresentationDescriptor")] private static extern int WinPd(uint n, IntPtr* sds, IntPtr* pd);

        private static readonly Vtable s_vt = BuildSource();
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        public readonly IntPtr Queue, Pd, Sd;
        public readonly ScriptStream Stream;
        public readonly CallLog Calls = new();
        private bool _started;

        /// <summary>The script's steps: a sample (its time, duration, size), a tick (its time), a change to a size, the end.</summary>
        public readonly Queue<(string Kind, long Time, int W, int H)> Script = new();
        public int W = 64, H = 48;

        public static IntPtr Nv12(int w, int h)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), new Guid("73646976-0000-0010-8000-00aa00389b71"));
            MFReader.SetGuid(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), new Guid("3231564e-0000-0010-8000-00aa00389b71"));
            MFReader.SetU64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack((uint)w, (uint)h));
            MFReader.SetU64(t, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
            MFReader.SetU32(t, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            MFReader.SetU64(t, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF.Pack(1, 1));
            MFReader.SetU32(t, new Guid("c9173739-5e56-461c-b713-46fb995cb95f"), 1);         // MF_MT_ALL_SAMPLES_INDEPENDENT
            MFReader.SetU32(t, new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321b"), 1);         // MF_MT_FIXED_SIZE_SAMPLES
            MFReader.SetU32(t, new Guid("dad3ab78-1990-408b-bce2-eba673dacc10"), (uint)(w * h * 3 / 2));   // MF_MT_SAMPLE_SIZE
            MFReader.SetU32(t, new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), (uint)w);  // MF_MT_DEFAULT_STRIDE
            return t;
        }

        public ScriptSource() : base(s_vt)
        {
            IntPtr q, sd, pd;
            int hr = OperatingSystem.IsWindows() ? WinQueue(&q) : MFExports.MFCreateEventQueue(&q);
            Assert.True(hr >= 0);
            Queue = q;
            var t = Nv12(W, H);
            hr = OperatingSystem.IsWindows() ? WinSd(1, 1, &t, &sd) : MFExports.MFCreateStreamDescriptor(1, 1, &t, &sd);
            Assert.True(hr >= 0);
            // the stream's type current, of its handler
            IntPtr h;
            ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(sd, 34))(sd, &h);                 // IMFStreamDescriptor::GetMediaTypeHandler
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(h, 6))(h, t);                         // SetCurrentMediaType
            MFReader.Release(h);
            MFReader.Release(t);
            Sd = sd;
            hr = OperatingSystem.IsWindows() ? WinPd(1, &sd, &pd) : MFExports.MFCreatePresentationDescriptor(1, &sd, &pd);
            Assert.True(hr >= 0);
            ((delegate* unmanaged<IntPtr, uint, int>)Fn(pd, 35))(pd, 0);                       // SelectStream
            Pd = pd;
            Stream = new ScriptStream(this);
        }

        protected override void OnFinalRelease() { Stream.Release(); MFReader.Release(Pd); MFReader.Release(Sd); ((delegate* unmanaged<IntPtr, int>)Fn(Queue, 9))(Queue); MFReader.Release(Queue); }

        public void Post(IntPtr queue, uint type, int hr, long? time = null, IntPtr unk = default)
        {
            var none = Guid.Empty;
            if (unk != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, uint, Guid*, int, IntPtr, int>)Fn(queue, 8))(queue, type, &none, hr, unk); return; }
            var pv = stackalloc byte[24]; new Span<byte>(pv, 24).Clear();
            if (time is { } t) { *(ushort*)pv = 20; *(long*)(pv + 8) = t; }                     // VT_I8
            ((delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)Fn(queue, 7))(queue, type, &none, hr, pv);
        }

        private static ScriptSource S(IntPtr self) => From<ScriptSource>(self)!;

        private static Vtable BuildSource()
        {
            var vt = new Vtable("IMFMediaSource", 13, new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), new Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&CreatePresentationDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)&Start);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&Stop);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&Pause);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&Shutdown);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetEvent(IntPtr self, uint flags, IntPtr* e) { lock (S(self).Calls) S(self).Calls.Add($"source GetEvent {flags}"); var q = S(self).Queue; return ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(q, 3))(q, flags, e); }
        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr cb, IntPtr st) { lock (S(self).Calls) S(self).Calls.Add("source BeginGetEvent"); var q = S(self).Queue; return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Fn(q, 4))(q, cb, st); }
        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr r, IntPtr* e)
        {
            var q = S(self).Queue;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Fn(q, 5))(q, r, e);
            uint type = 0; if (hr >= 0 && *e != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint*, int>)Fn(*e, 33))(*e, &type);
            lock (S(self).Calls) S(self).Calls.Add($"source EndGetEvent 0x{hr:x8} {type}");
            return hr;
        }
        [UnmanagedCallersOnly] private static int QueueEvent(IntPtr self, uint type, Guid* ext, int hr, byte* pv) { var q = S(self).Queue; return ((delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)Fn(q, 7))(q, type, ext, hr, pv); }
        [UnmanagedCallersOnly] private static int GetCharacteristics(IntPtr self, uint* c) { lock (S(self).Calls) S(self).Calls.Add("GetCharacteristics"); *c = 6; return 0; }          // MFMEDIASOURCE_CAN_SEEK, _CAN_PAUSE
        [UnmanagedCallersOnly] private static int CreatePresentationDescriptor(IntPtr self, IntPtr* pd) { var s = S(self); lock (s.Calls) s.Calls.Add("CreatePresentationDescriptor"); return ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s.Pd, 37))(s.Pd, pd); }

        [UnmanagedCallersOnly]
        private static int Start(IntPtr self, IntPtr pd, Guid* format, byte* start)
        {
            var s = S(self);
            long at = start != null && *(ushort*)start == 20 ? *(long*)(start + 8) : 0;
            lock (s.Calls) s.Calls.Add($"Start {(start != null && *(ushort*)start == 20 ? at.ToString() : "current")}");
            s.Post(s.Queue, s._started ? 206u : 205u, 0, null, s.Stream.Pointer);                 // MEUpdatedStream / MENewStream
            s.Post(s.Queue, 201, 0, at);                                                            // MESourceStarted
            s.Post(s.Stream.Queue, 210, 0, at);                                                     // MEStreamStarted
            s._started = true;
            return 0;
        }

        [UnmanagedCallersOnly] private static int Stop(IntPtr self) { var s = S(self); lock (s.Calls) s.Calls.Add("Stop"); s.Post(s.Queue, 203, 0); return 0; }
        [UnmanagedCallersOnly] private static int Pause(IntPtr self) => unchecked((int)0xC00D36B2);
        [UnmanagedCallersOnly] private static int Shutdown(IntPtr self) { var s = S(self); lock (s.Calls) s.Calls.Add("Shutdown"); return 0; }
    }

    internal sealed unsafe class ScriptStream : ComObject
    {
        [DllImport("mfplat.dll", EntryPoint = "MFCreateEventQueue")] private static extern int WinQueue(IntPtr* q);
        private static readonly Vtable s_vt = BuildStream();
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private readonly ScriptSource _source;
        public readonly IntPtr Queue;
        private bool _sent;

        public ScriptStream(ScriptSource source) : base(s_vt)
        {
            _source = source;
            IntPtr q;
            int hr = OperatingSystem.IsWindows() ? WinQueue(&q) : MFExports.MFCreateEventQueue(&q);
            Assert.True(hr >= 0);
            Queue = q;
        }

        protected override void OnFinalRelease() { ((delegate* unmanaged<IntPtr, int>)Fn(Queue, 9))(Queue); MFReader.Release(Queue); }

        private static ScriptStream X(IntPtr self) => From<ScriptStream>(self)!;

        private static Vtable BuildStream()
        {
            var vt = new Vtable("IMFMediaStream", 10, new Guid("d182108f-4ec6-443f-aa42-a71106ec825f"), new Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaSource);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetStreamDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&RequestSample);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetEvent(IntPtr self, uint flags, IntPtr* e) { lock (X(self)._source.Calls) X(self)._source.Calls.Add($"stream GetEvent {flags}"); var q = X(self).Queue; return ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(q, 3))(q, flags, e); }
        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr cb, IntPtr st) { lock (X(self)._source.Calls) X(self)._source.Calls.Add("stream BeginGetEvent"); var q = X(self).Queue; return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Fn(q, 4))(q, cb, st); }
        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr r, IntPtr* e)
        {
            var q = X(self).Queue;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Fn(q, 5))(q, r, e);
            uint type = 0; if (hr >= 0 && *e != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint*, int>)Fn(*e, 33))(*e, &type);
            lock (X(self)._source.Calls) X(self)._source.Calls.Add($"stream EndGetEvent 0x{hr:x8} {type}");
            return hr;
        }
        [UnmanagedCallersOnly] private static int QueueEvent(IntPtr self, uint type, Guid* ext, int hr, byte* pv) { var q = X(self).Queue; return ((delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)Fn(q, 7))(q, type, ext, hr, pv); }
        [UnmanagedCallersOnly] private static int GetMediaSource(IntPtr self, IntPtr* s) { lock (X(self)._source.Calls) X(self)._source.Calls.Add("GetMediaSource"); *s = X(self)._source.AddRefPointer(); return 0; }
        [UnmanagedCallersOnly] private static int GetStreamDescriptor(IntPtr self, IntPtr* sd) { var x = X(self); lock (x._source.Calls) x._source.Calls.Add("GetStreamDescriptor"); MFReader.AddRef(x._source.Sd); *sd = x._source.Sd; return 0; }

        /// <summary>The script's next steps: each tick and change of format posted, then the sample (or the end).</summary>
        [UnmanagedCallersOnly]
        private static int RequestSample(IntPtr self, IntPtr token)
        {
            var x = X(self); var s = x._source;
            lock (s.Calls) s.Calls.Add("RequestSample");
            while (true)
            {
                if (s.Script.Count == 0) return unchecked((int)0xC00D3E84);                  // MF_E_END_OF_STREAM
                var (kind, time, w, h) = s.Script.Dequeue();
                switch (kind)
                {
                    case "tick":
                        s.Post(x.Queue, 214, 0, time);                                         // MEStreamTick
                        continue;
                    case "format":
                    {
                        s.W = w; s.H = h;
                        var t = ScriptSource.Nv12(w, h);
                        IntPtr hd;
                        ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s.Sd, 34))(s.Sd, &hd);
                        ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(hd, 6))(hd, t);
                        MFReader.Release(hd);
                        s.Post(x.Queue, 216, 0, null, t);                                     // MEStreamFormatChanged
                        MFReader.Release(t);
                        continue;
                    }
                    case "end":
                        s.Post(x.Queue, 212, 0);                                              // MEEndOfStream
                        s.Post(s.Queue, 211, 0);                                              // MEEndOfPresentation
                        return 0;
                    default:
                    {
                        var smp = MFWriter.Sample(MFDecoderTests.Picture(s.W, s.H, (int)(time / 400_000)), time, 400_000);
                        if (token != IntPtr.Zero) { var k = new Guid("8294da66-f328-4805-b551-00deb4c57a61"); ((delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)Fn(smp, 27))(smp, &k, token); }
                        MFReader.SetU32(smp, new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a"), 1);       // MFSampleExtension_CleanPoint
                        if (!x._sent) MFReader.SetU32(smp, new Guid("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a"), 1);   // MFSampleExtension_Discontinuity, of the first
                        x._sent = true;
                        s.Post(x.Queue, 213, 0, null, smp);                                   // MEMediaSample
                        lock (s.Calls) s.Calls.Add($"  posted the sample of {time}{(token != IntPtr.Zero ? ", its token" : "")}");
                        MFReader.Release(smp);
                        return 0;
                    }
                }
            }
        }
    }

    /// <summary>IMFSourceScriptReaderCallback: each OnReadSample's status, flags and time, as it comes.</summary>
    internal sealed unsafe class ScriptReaderCallback : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly System.Collections.Concurrent.BlockingCollection<string> Reads = new();
        public ScriptReaderCallback() : base(s_vt) { }
        private static Vtable Build()
        {
            var vt = new Vtable("IMFSourceScriptReaderCallback", 6, new Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"));
            vt.Set(3, (delegate* unmanaged<IntPtr, int, uint, uint, long, IntPtr, int>)&OnReadSample);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, int>)&OnFlush);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&OnEvent);
            return vt;
        }
        [UnmanagedCallersOnly]
        private static int OnReadSample(IntPtr self, int hr, uint index, uint flags, long time, IntPtr sample)
        {
            string what = sample == IntPtr.Zero ? "no sample" : $"{MFReader.SampleBytes(sample, out _).Length} bytes";
            From<ScriptReaderCallback>(self)!.Reads.Add($"0x{hr:x8} stream {index} flags 0x{flags:x} time {time} {what}");
            return 0;
        }
        [UnmanagedCallersOnly] private static int OnFlush(IntPtr self, uint index) => 0;
        [UnmanagedCallersOnly] private static int OnEvent(IntPtr self, uint index, IntPtr e) => 0;
    }

    public sealed unsafe class MFScriptSourceProbe
    {
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromMediaSource")] private static extern int WinReaderOf(IntPtr source, IntPtr attributes, IntPtr* reader);
        private static string Hr(int hr) => $"0x{hr:x8}";

        private static string Type(IntPtr r, bool native)
        {
            IntPtr t; int hr = native ? MFReader.GetNativeMediaType(r, 0, 0, out t) : MFReader.GetCurrentMediaType(r, 0, out t);
            if (hr < 0) return Hr(hr);
            var (w, h) = MF.Unpack(MFReader.U64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d")));
            var s = $"{MFReader.Name(MFReader.G(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")))} {w}x{h}";
            MFReader.Release(t);
            return s;
        }

        private static List<string> Run(string name, bool rgb)
        {
            var log = new ProgressLog("MF_PROBE_SCRIPT") { $"==== {name}" };
            var src = new ScriptSource();
            foreach (var step in new (string, long, int, int)[] { ("sample", 0, 0, 0), ("sample", 400_000, 0, 0), ("tick", 800_000, 0, 0), ("sample", 1_200_000, 0, 0),
                ("format", 0, 32, 24), ("sample", 1_600_000, 0, 0), ("sample", 2_000_000, 0, 0), ("end", 0, 0, 0) })
                src.Script.Enqueue(step);
            IntPtr attrs = IntPtr.Zero;
            if (rgb)
            {
                attrs = MFReader.MediaType();      // (an IMFAttributes)
                MFReader.SetU32(attrs, new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1);    // MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING
            }
            IntPtr r; int hr = OperatingSystem.IsWindows() ? WinReaderOf(src.Pointer, attrs, &r) : MFExports.MFCreateSourceReaderFromMediaSource(src.Pointer, attrs, &r);
            MFReader.Release(attrs);
            log.Add($"  reader {Hr(hr)}");
            if (hr < 0) { src.Release(); return log; }
            if (rgb)
            {
                var t = MFReader.MediaType();
                MFReader.SetGuid(t, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), new Guid("73646976-0000-0010-8000-00aa00389b71"));
                MFReader.SetGuid(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), new Guid("00000016-0000-0010-8000-00aa00389b71"));
                log.Add($"  set RGB32 {Hr(MFReader.SetCurrentMediaType(r, 0, t))}");
                MFReader.Release(t);
            }
            log.Add($"  native {Type(r, true)}, current {Type(r, false)}");
            for (int k = 0; k < 20; k++)
            {
                hr = MFReader.ReadSample(r, 0, out uint actual, out uint flags, out long ts, out var s);
                string what = s == IntPtr.Zero ? "no sample" : $"{MFReader.SampleBytes(s, out _).Length} bytes";
                log.Add($"  read {Hr(hr)} stream {actual} flags 0x{flags:x} time {ts} {what}");
                MFReader.Release(s);
                if ((flags & 0x30) != 0) log.Add($"    native {Type(r, true)}, current {Type(r, false)}");
                if (hr < 0 || (flags & 2) != 0) break;
            }
            MFReader.Release(r);
            lock (src.Calls) log.Add($"  the source's calls: {string.Join(", ", src.Calls.Where(c => c.StartsWith("Start") || c is "Stop" or "Shutdown"))}; requests {src.Calls.Count(c => c == "RequestSample")}");
            src.Release();
            return log;
        }

        /// <summary>The reader of the script source in its asynchronous mode: each OnReadSample, of a read asked after each.</summary>
        private static List<string> Async()
        {
            var log = new ProgressLog("MF_PROBE_SCRIPT") { "==== NV12, asynchronously" };
            var src = new ScriptSource();
            foreach (var step in new (string, long, int, int)[] { ("sample", 0, 0, 0), ("sample", 400_000, 0, 0), ("tick", 800_000, 0, 0), ("sample", 1_200_000, 0, 0),
                ("format", 0, 32, 24), ("sample", 1_600_000, 0, 0), ("sample", 2_000_000, 0, 0), ("end", 0, 0, 0) })
                src.Script.Enqueue(step);
            var cb = new ScriptReaderCallback();
            var attrs = MFReader.MediaType();
            var key = new Guid("1e3dbeac-bb43-4c35-b507-cd644464c965");                               // MF_SOURCE_READER_ASYNC_CALLBACK
            ((delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)MFReader.Fn(attrs, 27))(attrs, &key, cb.Pointer);
            IntPtr r; int hr = OperatingSystem.IsWindows() ? WinReaderOf(src.Pointer, attrs, &r) : MFExports.MFCreateSourceReaderFromMediaSource(src.Pointer, attrs, &r);
            MFReader.Release(attrs);
            log.Add($"  reader {Hr(hr)}");
            if (hr >= 0)
            {
                for (int k = 0; k < 12; k++)
                {
                    hr = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)MFReader.Fn(r, 9))(r, 0, 0, null, null, null, null);
                    if (hr < 0) { log.Add($"  read {Hr(hr)}"); break; }
                    if (!cb.Reads.TryTake(out var got, 10_000)) { log.Add("  no OnReadSample in 10 s"); break; }
                    log.Add($"  OnReadSample {got}");
                    if (got.Contains("flags 0x2 ") || got.StartsWith("0x8") || got.StartsWith("0xc")) break;
                }
                MFReader.Release(r);
            }
            cb.Release();
            src.Release();
            return log;
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            var log = Async();
            log.AddRange(Run("NV12, as the source gives it", false));
            log.AddRange(Run("RGB32, the video processor's", true));
            return log;
        }

        /// <summary>
        /// The reader passes on an application's source's stream tick (a read of no sample, MF_SOURCE_READERF_STREAMTICK, of the
        /// tick's time; the sample still asked for coming next) and change of format (the first sample of it with
        /// NATIVEMEDIATYPECHANGED and CURRENTMEDIATYPECHANGED, the types of the new size), as MF_SOURCE_READER_FLAG's
        /// documentation has them; asynchronously alike.
        /// </summary>
        [Fact]
        public void TheReader_PassesOnASourcesTicksAndFormatChanges()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' reader reads not this source (above)");
            var expected = new[]
            {
                "==== NV12, asynchronously", "  reader 0x00000000",
                "  OnReadSample 0x00000000 stream 0 flags 0x0 time 0 4608 bytes", "  OnReadSample 0x00000000 stream 0 flags 0x0 time 400000 4608 bytes",
                "  OnReadSample 0x00000000 stream 0 flags 0x100 time 800000 no sample", "  OnReadSample 0x00000000 stream 0 flags 0x0 time 1200000 4608 bytes",
                "  OnReadSample 0x00000000 stream 0 flags 0x30 time 1600000 1152 bytes", "  OnReadSample 0x00000000 stream 0 flags 0x0 time 2000000 1152 bytes",
                "  OnReadSample 0x00000000 stream 0 flags 0x2 time 0 no sample",
                "==== NV12, as the source gives it", "  reader 0x00000000", "  native MFVideoFormat_NV12 64x48, current MFVideoFormat_NV12 64x48",
                "  read 0x00000000 stream 0 flags 0x0 time 0 4608 bytes", "  read 0x00000000 stream 0 flags 0x0 time 400000 4608 bytes",
                "  read 0x00000000 stream 0 flags 0x100 time 800000 no sample", "  read 0x00000000 stream 0 flags 0x0 time 1200000 4608 bytes",
                "  read 0x00000000 stream 0 flags 0x30 time 1600000 1152 bytes", "    native MFVideoFormat_NV12 32x24, current MFVideoFormat_NV12 32x24",
                "  read 0x00000000 stream 0 flags 0x0 time 2000000 1152 bytes", "  read 0x00000000 stream 0 flags 0x2 time 0 no sample",
                "  the source's calls: Start current, Shutdown; requests 6",
            };
            var ours = Report();
            for (int i = 0; i < expected.Length; i++) Assert.True(i < ours.Count && expected[i] == ours[i], $"line {i}: expected '{expected[i]}', ours '{(i < ours.Count ? ours[i] : "(none)")}'");
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SCRIPT") is not { Length: > 0 }, "MF_PROBE_SCRIPT=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SCRIPT")!, Report());
        }
    }
}
