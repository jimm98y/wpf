// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the source resolver and the media sources of files do (MF_PROBE_SRC=<file> writes the report;
// MediaSource.windows.txt is Windows'): what the resolver makes of a URL and of a byte stream; the source's
// characteristics and presentation descriptor; the events of Start, of samples asked for, of the end, of a
// seek, a pause, a stop and a shutdown -- on the source's queue and on each stream's.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFMediaSourceProbe
    {
        [DllImport("mfplat.dll", EntryPoint = "MFCreateSourceResolver")] private static extern int WinResolver(IntPtr* r);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromMediaSource")] private static extern int WinReaderOf(IntPtr source, IntPtr attributes, IntPtr* reader);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateFile")] private static extern int WinFile(uint access, uint open, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr* bs);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static void Release(IntPtr o) => MFReader.Release(o);

        private static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66"), IID_IMFMediaStream = new("d182108f-4ec6-443f-aa42-a71106ec825f");
        private static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), IID_IMFByteStream = new("ad4c1b00-4bf7-422f-9175-756693d9130d");
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

        internal static readonly Dictionary<uint, string> Events = new()
        {
            [1] = "MEError", [3] = "MENonFatalError", [201] = "MESourceStarted", [202] = "MEStreamStarted", [203] = "MESourceSeeked", [204] = "MEStreamSeeked",
            [205] = "MENewStream", [206] = "MEUpdatedStream", [207] = "MESourceStopped", [208] = "MEStreamStopped", [209] = "MESourcePaused", [210] = "MEStreamPaused",
            [211] = "MEEndOfPresentation", [212] = "MEEndOfStream", [213] = "MEMediaSample", [214] = "MEStreamTick", [216] = "MEStreamFormatChanged",
            [217] = "MESourceRateChanged", [219] = "MESourceCharacteristicsChanged", [221] = "MESourceMetadataChanged",
        };

        internal static int CreateResolver(out IntPtr r)
        {
            IntPtr p;
            MFReader.Startup();
            int hr = OperatingSystem.IsWindows() ? WinResolver(&p) : MFExports.MFCreateSourceResolver(&p);
            r = hr >= 0 ? p : IntPtr.Zero;
            return hr;
        }

        internal static int FromUrl(IntPtr resolver, string url, uint flags, out uint type, out IntPtr obj)
        {
            uint t = 99; IntPtr o = IntPtr.Zero;
            int hr;
            fixed (char* u = url) hr = ((delegate* unmanaged<IntPtr, char*, uint, IntPtr, uint*, IntPtr*, int>)Fn(resolver, 3))(resolver, u, flags, IntPtr.Zero, &t, &o);
            type = t; obj = hr >= 0 ? o : IntPtr.Zero;
            return hr;
        }

        internal static IntPtr Qi(IntPtr o, Guid iid)
        {
            IntPtr p;
            return ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(o, 0))(o, &iid, &p) >= 0 ? p : IntPtr.Zero;
        }

        /// <summary>An event of the queue (of a source or a stream), waited for up to the time given; null when none came.</summary>
        internal static (uint Type, int Status, Guid Ext, ushort Vt, IntPtr Unk, long I8)? Event(IntPtr gen, int waitMs, out int hr)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                IntPtr e;
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(gen, 3))(gen, 1, &e);           // MF_EVENT_FLAG_NO_WAIT
                if (hr >= 0)
                {
                    uint type; int status; Guid ext;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Fn(e, 33))(e, &type);
                    ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(e, 34))(e, &ext);
                    ((delegate* unmanaged<IntPtr, int*, int>)Fn(e, 35))(e, &status);
                    var pv = stackalloc byte[24];
                    new Span<byte>(pv, 24).Clear();
                    ((delegate* unmanaged<IntPtr, byte*, int>)Fn(e, 36))(e, pv);
                    ushort vt = *(ushort*)pv;
                    IntPtr unk = vt == 13 ? *(IntPtr*)(pv + 8) : IntPtr.Zero;      // AddRef'd: the caller's
                    long i8 = vt is 20 or 21 ? *(long*)(pv + 8) : 0;
                    Release(e);
                    return (type, status, ext, vt, unk, i8);
                }
                if (hr != unchecked((int)0xC00D3E80) || sw.ElapsedMilliseconds > waitMs) return null;      // MF_E_NO_EVENTS_AVAILABLE
                Thread.Sleep(5);
            }
        }

        private static string Name(uint type) => Events.TryGetValue(type, out var n) ? n : "event " + type;

        internal static string Describe((uint Type, int Status, Guid Ext, ushort Vt, IntPtr Unk, long I8) e)
        {
            string v = e.Vt switch { 0 => "", 13 => " UNKNOWN", 20 => $" I8 {e.I8}", 21 => $" UI8 {e.I8}", 5 => " R8", 19 => " UI4", _ => $" VT {e.Vt}" };
            return $"{Name(e.Type)} {Hr(e.Status)}{(e.Ext != Guid.Empty ? " ext " + e.Ext : "")}{v}";
        }

        /// <summary>The events now on the queue, waiting up to the time given for the first and briefly for the rest.</summary>
        internal static List<(uint Type, int Status, Guid Ext, ushort Vt, IntPtr Unk, long I8)> Drain(IntPtr gen, int firstMs, Func<uint, bool>? until = null)
        {
            var got = new List<(uint, int, Guid, ushort, IntPtr, long)>();
            int wait = firstMs;
            while (Event(gen, wait, out _) is { } e)
            {
                got.Add(e);
                if (until != null && until(e.Type)) wait = 40;
                else if (until == null) wait = 40;
            }
            return got;
        }

        internal static string SampleLine(IntPtr s)
        {
            long t; ((delegate* unmanaged<IntPtr, long*, int>)Fn(s, 35))(s, &t);
            long d = MFReader.Duration(s);
            var bytes = MFReader.SampleBytes(s, out uint buffers);
            var attrs = MFReader.Dump(s).Where(l => !l.StartsWith("MFSampleExtension_Token")).ToList();
            return $"sample t={t} d={d} size={bytes.Length} buffers={buffers} {MFReader.HexOf(bytes, 12)} [{string.Join("; ", attrs)}]";
        }

        private static readonly Guid MFSampleExtension_Token = new("8294da66-f328-4805-b551-00deb4c57a61");

        internal static IntPtr Pd(IntPtr src, out int hr) { IntPtr p; hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(src, 8))(src, &p); return hr >= 0 ? p : IntPtr.Zero; }

        internal static int Start(IntPtr src, IntPtr pd, long? at)
        {
            var pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            if (at is long a) { *(ushort*)pv = 20; *(long*)(pv + 8) = a; }
            Guid fmt = Guid.Empty;
            return ((delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)Fn(src, 9))(src, pd, &fmt, pv);
        }

        internal static int RequestSample(IntPtr stream, IntPtr token) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(stream, 9))(stream, token);

        internal static uint StreamId(IntPtr stream)
        {
            IntPtr sd; uint id = 999;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(stream, 8))(stream, &sd) >= 0) { ((delegate* unmanaged<IntPtr, uint*, int>)Fn(sd, 33))(sd, &id); Release(sd); }
            return id;
        }

        /// <summary>The presentation descriptor, as lines; the descriptors' ids and kinds in order.</summary>
        internal static List<string> PdReport(IntPtr pd, out List<(uint Id, bool Selected, Guid Major)> streams)
        {
            var log = new List<string>();
            streams = new();
            log.AddRange(MFReader.Dump(pd).Select(l => "  pd " + l));
            uint n; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(pd, 33))(pd, &n);
            log.Add($"  streams {n}");
            for (uint i = 0; i < n; i++)
            {
                int sel; IntPtr sd;
                int hr = ((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Fn(pd, 34))(pd, i, &sel, &sd);
                if (hr < 0) { log.Add($"  sd {i} {Hr(hr)}"); continue; }
                uint id; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(sd, 33))(sd, &id);
                log.Add($"  sd {i}: id {id} selected {sel}");
                log.AddRange(MFReader.Dump(sd).Select(l => "    sd " + l));
                IntPtr h;
                hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(sd, 34))(sd, &h);
                Guid major = Guid.Empty;
                if (hr >= 0)
                {
                    ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(h, 8))(h, &major);
                    uint count; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(h, 4))(h, &count);
                    log.Add($"    handler major {MFReader.Name(major)} types {count}");
                    for (uint k = 0; k < count; k++)
                    {
                        IntPtr t;
                        if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(h, 5))(h, k, &t) < 0) continue;
                        log.Add($"    type {k}:");
                        log.AddRange(MFReader.Dump(t).Select(l => "      " + l));
                        Release(t);
                    }
                    IntPtr cur;
                    hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(h, 7))(h, &cur);
                    log.Add($"    current {Hr(hr)}{(hr >= 0 ? " " + MFReader.Name(MFReader.G(cur, SUB)) : "")}");
                    if (hr >= 0) Release(cur);
                    Release(h);
                }
                streams.Add((id, sel != 0, major));
                Release(sd);
            }
            return log;
        }

        internal static List<string> Report(string path)
        {
            var log = new List<string>();
            int hr = CreateResolver(out var resolver);
            log.Add($"resolver {Hr(hr)}");
            if (hr < 0) return log;
            try
            {
                hr = FromUrl(resolver, path, 1, out uint type, out var obj);                 // MF_RESOLUTION_MEDIASOURCE
                log.Add($"url {Hr(hr)} type {type}");
                if (hr < 0) return log;
                var src = Qi(obj, IID_IMFMediaSource);
                Release(obj);
                try { log.AddRange(SourceReport(src)); }
                finally { Release(src); }
            }
            finally { Release(resolver); }
            return log;
        }

        private static List<string> SourceReport(IntPtr src)
        {
            var log = new List<string>();
            uint ch; int hr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(src, 7))(src, &ch);
            log.Add($"characteristics {Hr(hr)} 0x{ch:x}");
            var pd = Pd(src, out hr);
            log.Add($"pd {Hr(hr)}");
            if (hr < 0) return log;
            log.AddRange(PdReport(pd, out var sds));
            IntPtr pd2 = Pd(src, out _);
            log.Add($"pd again: same object {pd2 == pd}");
            Release(pd2);

            // events before Start
            log.Add($"event before start: {(Event(src, 0, out hr) is { } e0 ? Describe(e0) : Hr(hr))}");

            // Start, at the beginning
            hr = Start(src, pd, null);
            log.Add($"start {Hr(hr)}");
            var streams = new Dictionary<uint, IntPtr>();
            foreach (var e in Drain(src, 3000, t => t == 201))
            {
                string extra = "";
                if (e.Type is 205 or 206 && e.Unk != IntPtr.Zero)
                {
                    var ms = Qi(e.Unk, IID_IMFMediaStream);
                    uint id = StreamId(ms);
                    extra = $" stream {id}";
                    streams[id] = ms;
                }
                if (e.Unk != IntPtr.Zero) Release(e.Unk);
                log.Add("  source: " + Describe(e) + extra);
            }
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
                foreach (var e in Drain(s, 1000, t => t == 202)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }

            // three samples of each stream, the first with a token
            var token = MFReader.Attributes();
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
            {
                for (int i = 0; i < 3; i++) log.Add($"  stream {id}: request {Hr(RequestSample(s, i == 0 ? token : IntPtr.Zero))}");
                int got = 0;
                while (got < 3 && Event(s, 3000, out hr) is { } e)
                {
                    string line = $"  stream {id}: " + Describe(e);
                    if (e.Type == 213 && e.Unk != IntPtr.Zero)
                    {
                        var smp = Qi(e.Unk, IID_IMFSample);
                        IntPtr tok;
                        var tk = MFSampleExtension_Token; var iid = new Guid("00000000-0000-0000-C000-000000000046");
                        bool hasToken = ((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Fn(smp, 17))(smp, &tk, &iid, &tok) >= 0;
                        if (hasToken) { line += $" token {(tok == token ? "ours" : "other")}"; Release(tok); }
                        line += " " + SampleLine(smp);
                        Release(smp);
                        got++;
                    }
                    if (e.Unk != IntPtr.Zero) Release(e.Unk);
                    log.Add(line);
                    if (e.Type == 212) break;
                }
            }
            Release(token);

            // the rest, to the end
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
            {
                int n = 0; long last = -1, bytes = 0; string end = "none";
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000)
                {
                    hr = RequestSample(s, IntPtr.Zero);
                    if (hr < 0) { end = "request " + Hr(hr); break; }
                    var e = Event(s, 3000, out hr);
                    if (e == null) { end = "no event " + Hr(hr); break; }
                    if (e.Value.Type == 213)
                    {
                        var smp = Qi(e.Value.Unk, IID_IMFSample);
                        ((delegate* unmanaged<IntPtr, long*, int>)Fn(smp, 35))(smp, &last);
                        bytes += MFReader.SampleBytes(smp, out _).Length;
                        Release(smp);
                        n++;
                    }
                    if (e.Value.Unk != IntPtr.Zero) Release(e.Value.Unk);
                    if (e.Value.Type == 212) { end = Describe(e.Value); break; }
                    if (e.Value.Type != 213) log.Add($"  stream {id}: on the way: " + Describe(e.Value));
                }
                log.Add($"  stream {id}: {n} more samples, {bytes} bytes, last t={last}, then {end}");
                log.Add($"  stream {id}: request after the end {Hr(RequestSample(s, IntPtr.Zero))}");
                foreach (var e in Drain(s, 300)) { log.Add($"  stream {id}: after: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            }
            foreach (var e in Drain(src, 1000)) { log.Add("  source: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }

            // seek: Start at 0.2 s
            hr = Start(src, pd, 2_000_000);
            log.Add($"start at 0.2 s {Hr(hr)}");
            foreach (var e in Drain(src, 3000, t => t is 201 or 203)) { log.Add("  source: " + Describe(e) + (e.Type is 205 or 206 ? " stream " + StreamId(e.Unk) : "")); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
            {
                foreach (var e in Drain(s, 1000, t => t is 202 or 204)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
                log.Add($"  stream {id}: request {Hr(RequestSample(s, IntPtr.Zero))}");
                if (Event(s, 3000, out hr) is { } e1)
                {
                    string line = $"  stream {id}: " + Describe(e1);
                    if (e1.Type == 213) { var smp = Qi(e1.Unk, IID_IMFSample); line += " " + SampleLine(smp); Release(smp); }
                    if (e1.Unk != IntPtr.Zero) Release(e1.Unk);
                    log.Add(line);
                }
            }

            // pause, then on from where it was
            hr = ((delegate* unmanaged<IntPtr, int>)Fn(src, 11))(src);
            log.Add($"pause {Hr(hr)}");
            foreach (var e in Drain(src, 1000)) { log.Add("  source: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            foreach (var (id, s) in streams.OrderBy(x => x.Key)) foreach (var e in Drain(s, 500)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            if (streams.Count > 0)
            {
                var (fid, fs) = streams.OrderBy(x => x.Key).First();
                log.Add($"  stream {fid}: request while paused {Hr(RequestSample(fs, IntPtr.Zero))}");
            }
            hr = Start(src, pd, null);
            log.Add($"start from the pause {Hr(hr)}");
            foreach (var e in Drain(src, 2000, t => t == 201)) { log.Add("  source: " + Describe(e) + (e.Type is 205 or 206 ? " stream " + StreamId(e.Unk) : "")); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
                foreach (var e in Drain(s, 1000))
                {
                    string line = $"  stream {id}: " + Describe(e);
                    if (e.Type == 213) { var smp = Qi(e.Unk, IID_IMFSample); line += " " + SampleLine(smp); Release(smp); }
                    if (e.Unk != IntPtr.Zero) Release(e.Unk);
                    log.Add(line);
                }

            // stop
            hr = ((delegate* unmanaged<IntPtr, int>)Fn(src, 10))(src);
            log.Add($"stop {Hr(hr)}");
            foreach (var e in Drain(src, 1000)) { log.Add("  source: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            foreach (var (id, s) in streams.OrderBy(x => x.Key)) foreach (var e in Drain(s, 500)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            foreach (var (id, s) in streams.OrderBy(x => x.Key)) log.Add($"  stream {id}: request while stopped {Hr(RequestSample(s, IntPtr.Zero))}");

            // a stream deselected: Start again with the first one off
            if (sds.Count > 1)
            {
                ((delegate* unmanaged<IntPtr, uint, int>)Fn(pd, 36))(pd, 0);
                hr = Start(src, pd, 0);
                log.Add($"start with sd 0 deselected {Hr(hr)}");
                foreach (var e in Drain(src, 2000, t => t == 201)) { log.Add("  source: " + Describe(e) + (e.Type is 205 or 206 ? " stream " + StreamId(e.Unk) : "")); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
                if (streams.TryGetValue(sds[0].Id, out var off)) log.Add($"  stream {sds[0].Id}: request, deselected {Hr(RequestSample(off, IntPtr.Zero))}");
                foreach (var (id, s) in streams.OrderBy(x => x.Key)) foreach (var e in Drain(s, 300)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
                hr = ((delegate* unmanaged<IntPtr, int>)Fn(src, 10))(src);
                log.Add($"stop {Hr(hr)}");
                foreach (var e in Drain(src, 500)) { log.Add("  source: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
                foreach (var (id, s) in streams.OrderBy(x => x.Key)) foreach (var e in Drain(s, 300)) { log.Add($"  stream {id}: " + Describe(e)); if (e.Unk != IntPtr.Zero) Release(e.Unk); }
            }

            // shut down
            hr = ((delegate* unmanaged<IntPtr, int>)Fn(src, 12))(src);
            log.Add($"shutdown {Hr(hr)}");
            log.Add($"  characteristics {Hr(((delegate* unmanaged<IntPtr, uint*, int>)Fn(src, 7))(src, &ch))}");
            var pd3 = Pd(src, out hr); Release(pd3);
            log.Add($"  pd {Hr(hr)}");
            log.Add($"  start {Hr(Start(src, pd, null))}");
            log.Add($"  event {(Event(src, 0, out hr) is { } ez ? Describe(ez) : Hr(hr))}");
            log.Add($"  shutdown again {Hr(((delegate* unmanaged<IntPtr, int>)Fn(src, 12))(src))}");
            foreach (var (id, s) in streams.OrderBy(x => x.Key))
            {
                log.Add($"  stream {id}: request {Hr(RequestSample(s, IntPtr.Zero))}, event {(Event(s, 0, out hr) is { } es ? Describe(es) : Hr(hr))}");
                IntPtr back; hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s, 7))(s, &back);
                log.Add($"  stream {id}: source {Hr(hr)}");
                if (hr >= 0) Release(back);
                Release(s);
            }
            Release(pd);
            return log;
        }

        /// <summary>The resolver's answers besides: a byte stream asked for, one handed over, a file of no kind, none at all.</summary>
        internal static List<string> ResolverReport(string mp4, string notMedia, string missing)
        {
            var log = new List<string>();
            CreateResolver(out var r);
            try
            {
                int hr = FromUrl(r, mp4, 2, out uint type, out var o);                        // MF_RESOLUTION_BYTESTREAM
                log.Add($"url as byte stream {Hr(hr)} type {type} byte stream {(hr >= 0 && Qi(o, IID_IMFByteStream) is var b && b != IntPtr.Zero ? Rel(b) : false)}");
                Release(o);
                hr = FromUrl(r, mp4, 0, out type, out o);
                log.Add($"url, no flags {Hr(hr)} type {type}"); Release(o);
                hr = FromUrl(r, notMedia, 1, out type, out o);
                log.Add($"not media {Hr(hr)} type {type}"); Release(o);
                hr = FromUrl(r, missing, 1, out type, out o);
                log.Add($"missing {Hr(hr)} type {type}"); Release(o);
                hr = FromUrl(r, "nosuch://x/y.mp4", 1, out type, out o);
                log.Add($"unknown scheme {Hr(hr)} type {type}"); Release(o);

                // a byte stream handed over
                IntPtr bs;
                hr = OperatingSystem.IsWindows() ? WinFile(1, 0, 0, mp4, &bs) : CreateFile(mp4, &bs);
                log.Add($"MFCreateFile {Hr(hr)}");
                IntPtr so; uint st = 99;
                hr = ((delegate* unmanaged<IntPtr, IntPtr, char*, uint, IntPtr, uint*, IntPtr*, int>)Fn(r, 4))(r, bs, null, 1, IntPtr.Zero, &st, &so);
                log.Add($"byte stream, no URL {Hr(hr)} type {st}");
                if (hr >= 0)
                {
                    var src = Qi(so, IID_IMFMediaSource);
                    var pd = Pd(src, out _);
                    log.AddRange(MFReader.Dump(pd).Select(l => "  pd " + l));
                    Release(pd);
                    ((delegate* unmanaged<IntPtr, int>)Fn(src, 12))(src);
                    Release(src); Release(so);
                }
                Release(bs);
            }
            finally { Release(r); }
            return log;
        }

        /// <summary>A source reader of the source: its streams in order (major type, selected), a sample of each.</summary>
        internal static List<string> ReaderOfSource(string path)
        {
            var log = new List<string>();
            CreateResolver(out var r);
            int hr = FromUrl(r, path, 1, out _, out var o);
            Release(r);
            if (hr < 0) { log.Add($"url {Hr(hr)}"); return log; }
            var src = Qi(o, IID_IMFMediaSource);
            Release(o);
            IntPtr reader;
            hr = OperatingSystem.IsWindows() ? WinReaderOf(src, IntPtr.Zero, &reader) : MFExports.MFCreateSourceReaderFromMediaSource(src, IntPtr.Zero, &reader);
            log.Add($"reader of the source {Hr(hr)}");
            if (hr >= 0)
            {
                for (uint i = 0; i < 8; i++)
                {
                    hr = MFReader.GetStreamSelection(reader, i, out bool sel);
                    if (hr < 0) { log.Add($"  stream {i}: {Hr(hr)}"); break; }
                    MFReader.GetNativeMediaType(reader, i, 0, out var t);
                    string major = MFReader.Name(MFReader.G(t, MAJOR)), sub = MFReader.Name(MFReader.G(t, SUB));
                    Release(t);
                    string first = "";
                    if (sel)
                    {
                        hr = MFReader.ReadSample(reader, i, out uint actual, out uint flags, out long time, out var smp);
                        first = $" read {Hr(hr)} actual {actual} flags 0x{flags:x} time {time}";
                        if (smp != IntPtr.Zero) { first += $" bytes {MFReader.SampleBytes(smp, out uint nb).Length} buffers {nb}"; Release(smp); }
                    }
                    log.Add($"  stream {i}: {major} {sub} selected {sel}{first}");
                }
                // the source behind it
                IntPtr back; var g = Guid.Empty; var iid = IID_IMFMediaSource;
                hr = ((delegate* unmanaged<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)Fn(reader, 11))(reader, 0xFFFFFFFF, &g, &iid, &back);
                log.Add($"  service of the source {Hr(hr)} same {back == src}");
                if (hr >= 0) Release(back);
                Release(reader);
                uint ch;
                log.Add($"  source after the reader's release: characteristics {Hr(((delegate* unmanaged<IntPtr, uint*, int>)Fn(src, 7))(src, &ch))}");
            }
            ((delegate* unmanaged<IntPtr, int>)Fn(src, 12))(src);
            Release(src);
            return log;
        }

        private static bool Rel(IntPtr p) { Release(p); return true; }

        private static int CreateFile(string path, IntPtr* bs) { fixed (char* p = path) return MFExports.MFCreateFile(1, 0, 0, p, bs); }

        internal static List<string> All()
        {
            var all = new List<string>();
            var temp = new List<string>();
            foreach (var (name, f) in MFSourceReaderProbe.Files().Where(x => x.Name is "plain" or "fragmented" or "audio-trak-first" or "audio-id-1" or "audio-first-id-1"))
            {
                all.Add($"==== {name}");
                string path = f.WriteTemp("src-" + name);
                temp.Add(path);
                try { all.AddRange(Report(path)); } catch (Exception e) { all.Add("failed: " + e.Message); }
            }
            foreach (var m in MFSourceReaderProbe.AudioFiles.Concat(MFSourceReaderProbe.VideoFiles).Concat(MFSourceReaderProbe.AvFiles))
            {
                all.Add($"==== {m}");
                try { all.AddRange(Report(MFSourceReaderProbe.AudioPath(m))); } catch (Exception e) { all.Add("failed: " + e.Message); }
            }
            foreach (var (name, f) in MFSourceReaderProbe.Files().Where(x => x.Name is "plain" or "audio-trak-first"))
            {
                all.Add($"==== reader of the source: {name}");
                string path = f.WriteTemp("rsrc-" + name);
                temp.Add(path);
                try { all.AddRange(ReaderOfSource(path)); } catch (Exception e) { all.Add("failed: " + e.Message); }
            }
            foreach (var m in new[] { "clip.webm", "clip.mkv", "tone.mp3" })
            {
                all.Add($"==== reader of the source: {m}");
                try { all.AddRange(ReaderOfSource(MFSourceReaderProbe.AudioPath(m))); } catch (Exception e) { all.Add("failed: " + e.Message); }
            }
            all.Add("==== resolver");
            string notMedia = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wpfmf-notmedia-{Environment.ProcessId}.mp4");
            System.IO.File.WriteAllText(notMedia, "this is no media file at all, only text of some length to read");
            temp.Add(notMedia);
            all.AddRange(ResolverReport(MFSourceReaderProbe.AudioPath("clip-vp9.mp4"), notMedia, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpfmf-missing.mp4")));
            foreach (var t in temp) try { System.IO.File.Delete(t); } catch { }
            return all;
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SRC") is not { Length: > 0 }, "MF_PROBE_SRC=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SRC")!, All());
        }
    }
}
