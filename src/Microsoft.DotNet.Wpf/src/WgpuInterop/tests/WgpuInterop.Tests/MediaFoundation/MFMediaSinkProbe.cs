// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' MPEG-4 media sink (MF_PROBE_MEDIASINK=<file> writes the report): the sink the sink writer writes to, as
// GetServiceForStream(MF_SINK_WRITER_MEDIASINK) hands it out before, while and after writing; the one an application
// makes (MFCreateMPEG4MediaSink) -- its streams, types, interfaces, what it says to streams added and removed -- and a
// sink writer made of it (MFCreateSinkWriterFromMediaSink), writing a file.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFMediaSinkProbe
    {
        [DllImport("mf.dll", EntryPoint = "MFCreateMPEG4MediaSink")] private static extern int WinMpeg4(IntPtr stream, IntPtr video, IntPtr audio, IntPtr* sink);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSinkWriterFromMediaSink")] private static extern int WinWriterOn(IntPtr sink, IntPtr attributes, IntPtr* writer);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateFile")] private static extern int WinFile(uint access, uint open, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr* stream);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid AAC = new("00001610-0000-0010-8000-00aa00389b71"), PCM = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid IID_IMFMediaSink = new("6ef2a660-47c0-4666-b13d-cbb717f2fa2c");
        private const uint MEDIASINK = 0xFFFFFFFF;

        private static readonly (string, Guid)[] s_sinkInterfaces =
        {
            ("IMFFinalizableMediaSink", new("eaecb74a-9a50-42ce-9541-6a7f57aa4ad7")), ("IMFClockStateSink", new("f6696e82-74f7-4f3d-a178-8a5e09c3659f")),
            ("IMFMediaEventGenerator", new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")), ("IMFGetService", new("fa993888-4383-415a-a930-dd472a8cf6f7")),
            ("IMFMediaSinkPreroll", new("5dfd4b2a-7674-4110-a4e6-8a68fd5f3688")), ("IMFAttributes", new("2cd2d921-c447-44a7-a13c-4adabfc247e3")),
            ("IMFPresentationTimeSource", new("7ff12cce-f76f-41c2-863b-1666c8e5e139")), ("IPropertyStore", new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")),
        };
        private static readonly (string, Guid)[] s_streamInterfaces =
        {
            ("IMFMediaTypeHandler", new("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5")), ("IMFGetService", new("fa993888-4383-415a-a930-dd472a8cf6f7")),
            ("IMFAttributes", new("2cd2d921-c447-44a7-a13c-4adabfc247e3")), ("IMFMediaEventGenerator", new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")),
        };

        private static string Qi(IntPtr o, (string Name, Guid Iid)[] list) => string.Join(" ", list.Select(x =>
        {
            var g = x.Iid; IntPtr p;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(o, 0))(o, &g, &p);
            if (hr >= 0) MFReader.Release(p);
            return $"{x.Name} {(hr >= 0 ? "yes" : Hr(hr))}";
        }));

        /// <summary>The sink: its characteristics, interfaces, clock, and each stream sink's identifier, interfaces, type handler and current type.</summary>
        private static List<string> Describe(IntPtr sink, string indent = "    ")
        {
            var log = new List<string>();
            uint c;
            int hr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(sink, 3))(sink, &c);
            log.Add($"{indent}characteristics {Hr(hr)} 0x{(hr >= 0 ? c : 0):x}");
            if (hr < 0) return log;
            log.Add($"{indent}interfaces: {Qi(sink, s_sinkInterfaces)}");
            IntPtr clock;
            hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(sink, 10))(sink, &clock);
            log.Add($"{indent}clock {Hr(hr)}{(hr >= 0 && clock != IntPtr.Zero ? " set" : "")}");
            if (hr >= 0) MFReader.Release(clock);
            uint n;
            hr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(sink, 6))(sink, &n);
            log.Add($"{indent}streams {Hr(hr)} {(hr >= 0 ? n : 0)}");
            for (uint k = 0; hr >= 0 && k < n; k++)
            {
                IntPtr s;
                int shr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(sink, 7))(sink, k, &s);
                if (shr < 0) { log.Add($"{indent}  stream {k} {Hr(shr)}"); continue; }
                uint id;
                int ihr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(s, 8))(s, &id);
                IntPtr back;
                int bhr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s, 7))(s, &back);
                log.Add($"{indent}  stream {k}: identifier {Hr(ihr)} {id}, its sink {Hr(bhr)} the same {bhr >= 0 && back == sink}");
                if (bhr >= 0) MFReader.Release(back);
                IntPtr byId;
                int yhr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(sink, 8))(sink, id, &byId);
                log.Add($"{indent}    by identifier {Hr(yhr)} the same {yhr >= 0 && byId == s}");
                if (yhr >= 0) MFReader.Release(byId);
                log.Add($"{indent}    interfaces: {Qi(s, s_streamInterfaces)}");
                IntPtr h;
                int hhr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s, 9))(s, &h);
                if (hhr >= 0)
                {
                    Guid major; uint count;
                    int mhr = ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(h, 8))(h, &major);
                    int chr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(h, 4))(h, &count);
                    log.Add($"{indent}    handler: major {Hr(mhr)} {MFReader.Name(major)}, types {Hr(chr)} {(chr >= 0 ? count : 0)}");
                    IntPtr cur;
                    int thr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(h, 7))(h, &cur);
                    log.Add($"{indent}    current {Hr(thr)}");
                    if (thr >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => indent + "      " + l)); MFReader.Release(cur); }
                    MFReader.Release(h);
                }
                else log.Add($"{indent}    handler {Hr(hhr)}");
                MFReader.Release(s);
            }
            return log;
        }

        private static IntPtr H264Type()
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Video); MFReader.SetGuid(t, SUB, H264);
            MFReader.SetU64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
            MFReader.SetU64(t, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
            MFReader.SetU32(t, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
            MFReader.SetU32(t, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            MFReader.SetU64(t, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF.Pack(1, 1));
            return t;
        }

        private static IntPtr Nv12Type()
        {
            var t = H264Type();
            MFReader.SetGuid(t, SUB, NV12);
            var k = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"); ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(t, 19))(t, &k);
            return t;
        }

        private static IntPtr AacType()
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Audio); MFReader.SetGuid(t, SUB, AAC);
            MFReader.SetU32(t, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 2); MFReader.SetU32(t, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), 44100);
            MFReader.SetU32(t, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16); MFReader.SetU32(t, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731"), 16000);
            return t;
        }

        private static IntPtr PcmType()
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Audio); MFReader.SetGuid(t, SUB, PCM);
            MFReader.SetU32(t, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 2); MFReader.SetU32(t, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), 44100);
            MFReader.SetU32(t, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16); MFReader.SetU32(t, new Guid("322de230-9eeb-43bd-ab7a-ff412251541d"), 4);
            MFReader.SetU32(t, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731"), 176400);
            return t;
        }

        private static int Service(IntPtr w, out IntPtr sink)
        {
            var none = Guid.Empty; var iid = IID_IMFMediaSink; IntPtr p;
            int hr = ((delegate* unmanaged<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)Fn(w, 12))(w, MEDIASINK, &none, &iid, &p);
            sink = hr >= 0 ? p : IntPtr.Zero;
            return hr;
        }

        /// <summary>Five frames of video and of audio into the writer's streams 0 (video) and, where there is one, 1 (audio).</summary>
        private static string Write(IntPtr w, bool audio)
        {
            for (int k = 0; k < 5; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                int hr = MFWriter.WriteSample(w, 0, s);
                MFReader.Release(s);
                if (hr < 0) return $"write video {k} {Hr(hr)}";
                if (!audio) continue;
                s = MFWriter.Sample(new byte[17640 * 4], k * 400_000L, 400_000);
                hr = MFWriter.WriteSample(w, 1, s);
                MFReader.Release(s);
                if (hr < 0) return $"write audio {k} {Hr(hr)}";
            }
            return "written";
        }

        private static List<string> OfTheWriter()
        {
            var log = new List<string> { "==== the sink writer's media sink" };
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-msink-{Environment.ProcessId}.mp4");
            try
            {
                int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
                log.Add($"  create {Hr(hr)}");
                if (hr < 0) return log;
                log.Add($"  service, no streams: {Hr(Service(w, out var s0))}");
                MFReader.Release(s0);
                var vt = H264Type(); var at = AacType();
                int vhr = MFWriter.AddStream(w, vt, out uint vi), ahr = MFWriter.AddStream(w, at, out uint ai);
                MFReader.Release(vt); MFReader.Release(at);
                var nv = Nv12Type(); var pcm = PcmType();
                log.Add($"  add {Hr(vhr)} {Hr(ahr)}, inputs {Hr(MFWriter.SetInputMediaType(w, vi, nv))} {Hr(MFWriter.SetInputMediaType(w, ai, pcm))}");
                MFReader.Release(nv); MFReader.Release(pcm);
                hr = Service(w, out var s1);
                log.Add($"  service, before writing: {Hr(hr)}");
                if (hr >= 0) { log.AddRange(Describe(s1)); MFReader.Release(s1); }
                log.Add($"  begin {Hr(MFWriter.BeginWriting(w))}");
                hr = Service(w, out var s2);
                log.Add($"  service, writing: {Hr(hr)}");
                if (hr >= 0) log.AddRange(Describe(s2));
                log.Add($"  {Write(w, true)}");
                hr = Service(w, out var s3);
                log.Add($"  service, written: {Hr(hr)} the same {hr >= 0 && s3 == s2}");
                if (hr >= 0) MFReader.Release(s3);
                log.Add($"  finalize {Hr(MFWriter.Finalize(w))}");
                hr = Service(w, out var s4);
                log.Add($"  service, finalized: {Hr(hr)}");
                if (hr >= 0) MFReader.Release(s4);
                if (s2 != IntPtr.Zero) { log.Add("  the sink kept, finalized:"); log.AddRange(Describe(s2)); MFReader.Release(s2); }
                MFReader.Release(w);
                log.Add($"  file {(System.IO.File.Exists(path) ? new FileInfo(path).Length > 1000 : false)}, tracks {(System.IO.File.Exists(path) ? Tracks(path) : "")}");
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
            return log;
        }

        private static int Mpeg4(IntPtr stream, IntPtr video, IntPtr audio, out IntPtr sink)
        {
            IntPtr s; int hr = OperatingSystem.IsWindows() ? WinMpeg4(stream, video, audio, &s) : MFExports.MFCreateMPEG4MediaSink(stream, video, audio, &s);
            sink = hr >= 0 ? s : IntPtr.Zero;
            return hr;
        }

        private static int WriterOn(IntPtr sink, out IntPtr writer)
        {
            IntPtr w; int hr = OperatingSystem.IsWindows() ? WinWriterOn(sink, IntPtr.Zero, &w) : MFExports.MFCreateSinkWriterFromMediaSink(sink, IntPtr.Zero, &w);
            writer = hr >= 0 ? w : IntPtr.Zero;
            return hr;
        }

        private static int File(string path, out IntPtr stream)
        {
            IntPtr s; int hr;
            if (OperatingSystem.IsWindows()) hr = WinFile(3, 4, 0, path, &s);              // MF_ACCESSMODE_READWRITE, MF_FILEOPEN_DELETEIFEXIST
            else fixed (char* p = path) hr = MFExports.MFCreateFile(3, 4, 0, p, &s);
            stream = hr >= 0 ? s : IntPtr.Zero;
            return hr;
        }

        private static List<string> OfTheApplication()
        {
            var log = new List<string> { "==== the application's MPEG-4 sink" };
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-msink2-{Environment.ProcessId}.mp4");
            try
            {
                foreach (var (name, video, audio) in new[] { ("video and audio", true, true), ("video", true, false), ("audio", false, true), ("neither", false, false) })
                {
                    int hr = File(path, out var bs);
                    if (hr < 0) { log.Add($"  file {Hr(hr)}"); return log; }
                    var vt = video ? H264Type() : IntPtr.Zero; var at = audio ? AacType() : IntPtr.Zero;
                    hr = Mpeg4(bs, vt, at, out var sink);
                    MFReader.Release(vt); MFReader.Release(at);
                    log.Add($"  {name}: create {Hr(hr)}");
                    if (hr >= 0)
                    {
                        log.AddRange(Describe(sink));
                        var t = H264Type(); IntPtr added;
                        log.Add($"    add a stream: {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)Fn(sink, 4))(sink, 9, t, &added))}");
                        MFReader.Release(t);
                        log.Add($"    remove stream 1: {Hr(((delegate* unmanaged<IntPtr, uint, int>)Fn(sink, 5))(sink, 1))}");
                        IntPtr x;
                        log.Add($"    stream by identifier 7: {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(sink, 8))(sink, 7, &x))}");
                        log.Add($"    stream by index 5: {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(sink, 7))(sink, 5, &x))}");
                        log.Add($"    shutdown {Hr(((delegate* unmanaged<IntPtr, int>)Fn(sink, 11))(sink))}");
                        uint c;
                        log.Add($"    characteristics, shut down {Hr(((delegate* unmanaged<IntPtr, uint*, int>)Fn(sink, 3))(sink, &c))}");
                        MFReader.Release(sink);
                    }
                    ((delegate* unmanaged<IntPtr, int>)Fn(bs, 17))(bs);              // IMFByteStream::Close
                    MFReader.Release(bs);
                }

                // a writer of the sink: its streams the sink's, an encoder before each where the input is not the sink's type
                foreach (bool extra in new[] { true, false })
                {
                    int hr = File(path, out var bs);
                    var vt = H264Type(); var at = AacType();
                    hr = Mpeg4(bs, vt, at, out var sink);
                    MFReader.Release(vt); MFReader.Release(at);
                    log.Add($"  a writer of it{(extra ? ", a stream added" : "")}: sink {Hr(hr)}");
                    if (hr < 0) return log;
                    hr = WriterOn(sink, out var w);
                    log.Add($"    writer {Hr(hr)}");
                    if (hr >= 0)
                    {
                        hr = Service(w, out var got);
                        log.Add($"    service: {Hr(hr)} the sink given {hr >= 0 && got == sink}");
                        MFReader.Release(got);
                        var nv = Nv12Type(); var pcm = PcmType();
                        if (extra)
                        {
                            var t = H264Type(); uint idx;
                            log.Add($"    add a stream: {Hr(MFWriter.AddStream(w, t, out idx))} {idx}");
                            MFReader.Release(t);
                            log.Add($"    stream 2 input {Hr(MFWriter.SetInputMediaType(w, 2, nv))}");
                            log.AddRange(Describe(sink));
                        }
                        log.Add($"    inputs {Hr(MFWriter.SetInputMediaType(w, 0, nv))} {Hr(MFWriter.SetInputMediaType(w, 1, pcm))}");
                        MFReader.Release(nv); MFReader.Release(pcm);
                        for (uint k = 0; k < 2; k++)
                        {
                            Guid cat; IntPtr tr;
                            int thr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)Fn(w, 14))(w, k, 0, &cat, &tr);
                            log.Add($"    stream {k} transform 0: {Hr(thr)}{(thr >= 0 ? $" {cat}" : "")}");
                            if (thr >= 0) MFReader.Release(tr);
                        }
                        log.Add($"    begin {Hr(MFWriter.BeginWriting(w))}");
                        log.Add($"    {Write(w, true)}");
                        log.Add($"    finalize {Hr(MFWriter.Finalize(w))}");
                        uint c;
                        log.Add($"    the sink's characteristics, finalized {Hr(((delegate* unmanaged<IntPtr, uint*, int>)Fn(sink, 3))(sink, &c))}");
                        MFReader.Release(w);
                    }
                    MFReader.Release(sink);
                    ((delegate* unmanaged<IntPtr, int>)Fn(bs, 17))(bs);
                    MFReader.Release(bs);
                    log.Add($"    file {new FileInfo(path).Length > 1000}, tracks {Tracks(path)}");
                    // read back: its streams and duration
                    hr = MFReader.Create(path, IntPtr.Zero, out var r);
                    log.Add($"    read back {Hr(hr)}");
                    if (hr >= 0)
                    {
                        for (uint k = 0; k < 3; k++)
                        {
                            int nhr = MFReader.GetNativeMediaType(r, k, 0, out var nt);
                            log.Add($"      stream {k}: {Hr(nhr)}{(nhr >= 0 ? " " + MFReader.Name(MFReader.G(nt, SUB)) : "")}");
                            MFReader.Release(nt);
                        }
                        MFReader.Release(r);
                    }
                }
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
            return log;
        }

        /// <summary>The file's tracks, in moov's order: each one's tkhd track_ID, handler and stsz sample count.</summary>
        internal static string Tracks(string path)
        {
            var b = System.IO.File.ReadAllBytes(path);
            var r = new List<string>();
            uint U(int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
            IEnumerable<(string Type, int Body, int End)> Boxes(int from, int to)
            {
                int at = from;
                while (at + 8 <= to)
                {
                    long size = U(at); string type = System.Text.Encoding.ASCII.GetString(b, at + 4, 4); int hdr = 8;
                    if (size == 1) { size = (long)((ulong)U(at + 8) << 32 | U(at + 12)); hdr = 16; }
                    if (size < hdr || at + size > to) yield break;
                    yield return (type, at + hdr, (int)(at + size));
                    at += (int)size;
                }
            }
            (string, int, int)? Find(int from, int to, string type) { foreach (var x in Boxes(from, to)) if (x.Type == type) return x; return null; }
            if (Find(0, b.Length, "moov") is not var (_, mb, me)) return "no moov";
            foreach (var (type, tb, te) in Boxes(mb, me))
            {
                if (type != "trak") continue;
                uint id = Find(tb, te, "tkhd") is var (_, kb, _) ? U(kb + 12) : 0;
                string handler = "?"; uint samples = 0;
                if (Find(tb, te, "mdia") is var (_, db, de))
                {
                    if (Find(db, de, "hdlr") is var (_, hb, _)) handler = System.Text.Encoding.ASCII.GetString(b, hb + 8, 4);
                    if (Find(db, de, "minf") is var (_, nb, ne) && Find(nb, ne, "stbl") is var (_, sb, se) && Find(sb, se, "stsz") is var (_, zb, _)) samples = U(zb + 8);
                }
                r.Add($"{id}:{handler}:{samples}");
            }
            return string.Join(" ", r);
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            var log = OfTheWriter();
            log.AddRange(OfTheApplication());
            return log;
        }

        /// <summary>
        /// The shim's MPEG-4 media sink is Windows' (MediaSink.windows.txt): the sink writer's, handed out once writing has
        /// begun and after it is finalized, its clock, streams, identifiers, interfaces and types; the application's, of the
        /// types given, its streams added and removed, shut down; a writer of it, its encoders before the sink's streams, the
        /// file's tracks. Not compared: the bytes of the H.264 encoder's parameter sets (the system's encoder's own), and of
        /// the AAC tracks' packets the up to two the system's encoder's priming makes more at the drain.
        /// </summary>
        [Fact]
        public void TheMpeg4MediaSink_IsWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' media sink is the fixture");
            Assert.SkipUnless(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264), "this system has no H.264 encoder");
            var windows = System.IO.File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "MediaSink.windows.txt"));
            var ours = Report();
            Assert.Equal(windows.Length, ours.Count);
            for (int i = 0; i < windows.Length; i++)
            {
                string w = windows[i], o = ours[i];
                if (w == o) continue;
                if (w.Contains("MF_MT_MPEG_SEQUENCE_HEADER = BLOB") && o.Contains("MF_MT_MPEG_SEQUENCE_HEADER = BLOB")) continue;
                if (w.Contains("tracks ") && o.Contains("tracks "))
                {
                    var wt = w.Split("tracks ")[1].Split(' '); var ot = o.Split("tracks ")[1].Split(' ');
                    Assert.True(w.Split("tracks ")[0] == o.Split("tracks ")[0] && wt.Length == ot.Length, $"line {i}: Windows '{w}', ours '{o}'");
                    for (int k = 0; k < wt.Length; k++)
                    {
                        var a = wt[k].Split(':'); var b = ot[k].Split(':');
                        int extra = int.Parse(b[2]) - int.Parse(a[2]);
                        Assert.True(a[0] == b[0] && a[1] == b[1] && (extra == 0 || a[1] == "soun" && extra is > 0 and <= 2), $"line {i}: Windows '{w}', ours '{o}'");
                    }
                    continue;
                }
                Assert.Fail($"line {i}: Windows '{w}', ours '{o}'");
            }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_MEDIASINK") is not { Length: > 0 }, "MF_PROBE_MEDIASINK=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_MEDIASINK")!, Report());
        }
    }
}
