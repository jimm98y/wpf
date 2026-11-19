// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sink writer's transforms (MF_PROBE_SINKXF=<file> writes the report): of each input and target, the chain
// GetTransformForStream lists (the category and the types of each transform), what GetServiceForStream hands out
// (the encoder's ICodecAPI, its IMFTransform, the media sink), and GetStatistics before, while and after writing.
// Windows' writer on Windows, the shim's elsewhere.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFSinkTransformProbe
    {
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid I420 = new("30323449-0000-0010-8000-00aa00389b71"), YUY2 = new("32595559-0000-0010-8000-00aa00389b71");
        private static readonly Guid RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid AAC = new("00001610-0000-0010-8000-00aa00389b71"), PCM = new("00000001-0000-0010-8000-00aa00389b71"), FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid CH = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), AVG_BYTES = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), BLOCK = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        private static readonly Guid IID_IMFMediaSink = new("6ef2a660-47c0-4666-b13d-cbb717f2fa2c");
        private static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);

        private static IntPtr Type(Guid major, Guid sub, params (Guid, ulong, bool)[] values)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, major);
            MFReader.SetGuid(t, SUB, sub);
            foreach (var (k, v, wide) in values) if (wide) MFReader.SetU64(t, k, v); else MFReader.SetU32(t, k, (uint)v);
            return t;
        }

        private static IntPtr VideoType(Guid sub, uint w, uint h, uint bitrate = 0)
        {
            var values = new List<(Guid, ulong, bool)> { (FRAME_SIZE, MF.Pack(w, h), true), (FRAME_RATE, MF.Pack(25, 1), true), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true) };
            if (bitrate > 0) values.Add((AVG_BITRATE, bitrate, false));
            return Type(Video, sub, values.ToArray());
        }

        private static IntPtr AudioType(Guid sub, uint ch, uint rate, uint bits)
        {
            if (sub == AAC) return Type(Audio, AAC, (CH, ch, false), (RATE, rate, false), (BITS, 16, false), (AVG_BYTES, 16000, false));
            uint block = ch * bits / 8;
            return Type(Audio, sub, (CH, ch, false), (RATE, rate, false), (BITS, bits, false), (BLOCK, block, false), (AVG_BYTES, block * rate, false));
        }

        private static string Category(Guid c) =>
            c == new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a") ? "video encoder" : c == new Guid("91c64bd0-f91e-4d8c-9276-db248279d975") ? "audio encoder"
            : c == new Guid("302ea3fc-aa5f-47f9-9f7a-c2188bb16302") ? "video processor" : c == new Guid("12e17c21-532c-4a6e-8a1c-40825a736397") ? "video effect"
            : c == new Guid("11064c48-3648-4ed0-932e-05ce8ac811b7") ? "audio effect" : c.ToString();

        private static string TypeOf(IntPtr t, int slot)
        {
            IntPtr type;
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(t, slot))(t, 0, &type);
            if (hr < 0) return Hr(hr);
            var d = string.Join("; ", MFReader.Dump(type));
            MFReader.Release(type);
            return d;
        }

        /// <summary>The transforms of the stream, in order: each one's category and its current types.</summary>
        internal static List<string> Transforms(IntPtr w, uint i)
        {
            var log = new List<string>();
            for (uint n = 0; n < 5; n++)
            {
                Guid cat; IntPtr t;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)Fn(w, 14))(w, i, n, &cat, &t);
                if (hr < 0) { log.Add($"  transform {n}: {Hr(hr)}"); break; }
                log.Add($"  transform {n}: {Category(cat)}");
                log.Add($"    in: {TypeOf(t, 17)}");
                log.Add($"    out: {TypeOf(t, 18)}");
                MFReader.Release(t);
            }
            return log;
        }

        private static int Service(IntPtr w, uint i, Guid riid, out IntPtr o)
        {
            Guid service = Guid.Empty; IntPtr p;
            int hr = ((delegate* unmanaged<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)Fn(w, 12))(w, i, &service, &riid, &p);
            o = hr >= 0 ? p : IntPtr.Zero;
            return hr;
        }

        internal static List<string> Services(IntPtr w, uint i)
        {
            var log = new List<string>();
            int hr = Service(w, i, IID_ICodecAPI, out var api);
            string line = $"  ICodecAPI {Hr(hr)}";
            if (hr >= 0)
            {
                var k = AVEncCommonMeanBitRate;
                int s = ((delegate* unmanaged<IntPtr, Guid*, int>)Fn(api, 3))(api, &k);
                var v = stackalloc byte[24];
                new Span<byte>(v, 24).Clear();
                int g = ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Fn(api, 8))(api, &k, v);
                line += $", mean bit rate supported {Hr(s)}, value {Hr(g)}" + (g >= 0 ? $" vt {*(ushort*)v} {*(uint*)(v + 8)}" : "");
                MFReader.Release(api);
            }
            log.Add(line);
            hr = Service(w, i, IID_IMFTransform, out var t);
            log.Add($"  IMFTransform {Hr(hr)}");
            MFReader.Release(t);
            return log;
        }

        /// <summary>MF_SINK_WRITER_STATISTICS: its counts of samples and ticks, its last times received and encoded.</summary>
        internal static string Statistics(IntPtr w, uint i)
        {
            var st = stackalloc byte[112];
            new Span<byte>(st, 112).Clear();
            *(uint*)st = 112;
            int hr = ((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(w, 13))(w, i, st);
            if (hr < 0) return $"  statistics {Hr(hr)}";
            long L(int o) => *(long*)(st + o);
            return $"  statistics: last received {L(8)}, encoded {L(16)}, processed {L(24)}, tick {L(32)}; received {L(48)}, encoded {L(56)}, processed {L(64)}, ticks {L(72)}";
        }

        private static byte[] Frame(Guid sub, int w, int h, int k) =>
            sub == NV12 ? MFDecoderTests.Picture(w, h, k) : new byte[sub == RGB32 ? w * h * 4 : sub == YUY2 ? w * h * 2 : w * h * 3 / 2];

        internal static List<string> Report()
        {
            var log = new List<string>();
            {
                // no streams: BeginWriting's answer
                string p0 = Path.Combine(Path.GetTempPath(), $"wpfmf-sinkxf0-{Environment.ProcessId}.mp4");
                int h0 = MFWriter.Create(p0, IntPtr.Zero, out var w0);
                log.Add("==== no streams");
                if (h0 >= 0) { log.Add($"begin {Hr(MFWriter.BeginWriting(w0))}"); log.Add($"finalize {Hr(MFWriter.Finalize(w0))}"); MFReader.Release(w0); }
                try { File.Delete(p0); } catch { }
            }
            var cases = new (string Name, bool IsVideo, Guid Target, Guid Input, uint W, uint H, uint InW, uint InH, uint Rate, uint InRate, uint Bits)[]
            {
                ("NV12 to H.264", true, H264, NV12, 128, 96, 128, 96, 0, 0, 0),
                ("I420 to H.264", true, H264, I420, 128, 96, 128, 96, 0, 0, 0),
                ("YUY2 to H.264", true, H264, YUY2, 128, 96, 128, 96, 0, 0, 0),
                ("RGB32 to H.264", true, H264, RGB32, 128, 96, 128, 96, 0, 0, 0),
                ("NV12 of another size to H.264", true, H264, NV12, 128, 96, 64, 48, 0, 0, 0),
                ("H.264 as it is", true, H264, H264, 128, 96, 128, 96, 0, 0, 0),
                ("PCM to AAC", false, AAC, PCM, 0, 0, 0, 0, 44100, 44100, 16),
                ("float to AAC", false, AAC, FLOAT, 0, 0, 0, 0, 44100, 44100, 32),
                ("PCM of another rate to AAC", false, AAC, PCM, 0, 0, 0, 0, 44100, 48000, 16),
            };
            foreach (var c in cases)
            {
                log.Add($"==== {c.Name}");
                string path = Path.Combine(Path.GetTempPath(), $"wpfmf-sinkxf-{Environment.ProcessId}.mp4");
                int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
                if (hr < 0) { log.Add($"create {Hr(hr)}"); continue; }
                try
                {
                    var target = c.IsVideo ? VideoType(H264, c.W, c.H, 500_000) : AudioType(AAC, 2, c.Rate, 16);
                    hr = MFWriter.AddStream(w, target, out uint i);
                    MFReader.Release(target);
                    var input = c.IsVideo ? VideoType(c.Input, c.InW, c.InH, c.Input == H264 ? 500_000u : 0) : AudioType(c.Input, 2, c.InRate, c.Bits);
                    int inHr = MFWriter.SetInputMediaType(w, i, input);
                    MFReader.Release(input);
                    log.Add($"add {Hr(hr)}, input {Hr(inHr)}");
                    log.Add("after the input:");
                    log.AddRange(Transforms(w, i));
                    log.AddRange(Services(w, i));
                    log.Add(Statistics(w, i));
                    hr = MFWriter.BeginWriting(w);
                    log.Add($"begin {Hr(hr)}");
                    if (hr < 0) continue;
                    log.Add("after the beginning:");
                    log.AddRange(Transforms(w, i));
                    log.Add(Statistics(w, i));
                    if (c.Input == H264) { log.Add("(no samples of H.264 written)"); }
                    else
                    {
                        for (int k = 0; k < 5; k++)
                        {
                            byte[] data; long time, duration;
                            if (c.IsVideo) { data = Frame(c.Input, (int)c.InW, (int)c.InH, k); time = k * 400_000L; duration = 400_000; }
                            else { data = new byte[1024 * 2 * c.Bits / 8]; time = k * 1024 * 10_000_000L / c.InRate; duration = 1024 * 10_000_000L / c.InRate; }
                            var s = MFWriter.Sample(data, time, duration);
                            hr = MFWriter.WriteSample(w, i, s);
                            MFReader.Release(s);
                            if (hr < 0) { log.Add($"write {k} {Hr(hr)}"); break; }
                        }
                        log.Add("after five samples:");
                        log.Add(Statistics(w, i));
                    }
                    log.Add($"finalize {Hr(MFWriter.Finalize(w))}");
                    log.Add(Statistics(w, i));
                }
                finally
                {
                    MFReader.Release(w);
                    try { File.Delete(path); } catch { }
                }
            }
            log.AddRange(Levels());
            return log;
        }

        private static readonly Guid PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");

        /// <summary>The profile and level the writer sets on the encoder: of sizes, rates and bit rates, and of a target naming its own.</summary>
        internal static List<string> Levels()
        {
            var log = new List<string> { "==== levels" };
            var cases = new (uint W, uint H, uint Fps, uint Bitrate, uint Profile, uint Level)[]
            {
                (128, 96, 25, 500_000, 0, 0), (128, 96, 25, 100_000, 0, 0), (128, 96, 25, 50_000, 0, 0), (176, 144, 15, 300_000, 0, 0),
                (320, 240, 30, 1_000_000, 0, 0), (640, 480, 30, 2_000_000, 0, 0), (1280, 720, 30, 5_000_000, 0, 0), (1280, 720, 60, 8_000_000, 0, 0),
                (1920, 1080, 30, 10_000_000, 0, 0), (1920, 1080, 60, 20_000_000, 0, 0), (3840, 2160, 30, 40_000_000, 0, 0),
                (640, 480, 30, 2_000_000, 100, 0), (640, 480, 30, 2_000_000, 77, 0), (640, 480, 30, 2_000_000, 0, 31), (640, 480, 30, 2_000_000, 100, 40),
            };
            foreach (var c in cases)
            {
                string path = Path.Combine(Path.GetTempPath(), $"wpfmf-sinkxfl-{Environment.ProcessId}.mp4");
                int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
                if (hr < 0) { log.Add($"create {Hr(hr)}"); continue; }
                var target = Type(Video, H264, (FRAME_SIZE, MF.Pack(c.W, c.H), true), (FRAME_RATE, MF.Pack(c.Fps, 1), true), (AVG_BITRATE, c.Bitrate, false), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
                if (c.Profile != 0) MFReader.SetU32(target, PROFILE, c.Profile);
                if (c.Level != 0) MFReader.SetU32(target, LEVEL, c.Level);
                hr = MFWriter.AddStream(w, target, out uint i);
                MFReader.Release(target);
                var input = Type(Video, NV12, (FRAME_SIZE, MF.Pack(c.W, c.H), true), (FRAME_RATE, MF.Pack(c.Fps, 1), true), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
                int inHr = MFWriter.SetInputMediaType(w, i, input);
                MFReader.Release(input);
                string got = "";
                Guid cat; IntPtr t;
                if (inHr >= 0 && ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)Fn(w, 14))(w, i, 0, &cat, &t) >= 0)
                {
                    IntPtr type;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(t, 18))(t, 0, &type) >= 0)
                    {
                        got = string.Join(", ", MFReader.Dump(type).Where(l => l.StartsWith("MF_MT_MPEG2_")));
                        MFReader.Release(type);
                    }
                    MFReader.Release(t);
                }
                log.Add($"{c.W}x{c.H} {c.Fps} fps {c.Bitrate} b/s, profile {c.Profile} level {c.Level}: add {Hr(hr)} input {Hr(inHr)} -> {got}");
                MFReader.Release(w);
                try { File.Delete(path); } catch { }
            }
            return log;
        }

        /// <summary>
        /// The shim's writer chains the transforms Windows' does, of the same types, and answers for them as Windows' does
        /// (SinkTransform.windows.txt). Not compared: the encoder's parameter sets (its own), the counts of samples encoded
        /// while writing (Windows' writer encodes on a queue of its own), the times encoded but to 100 ns' hundreds (its
        /// resampler's rounding).
        /// </summary>
        [Fact]
        public void TheSinkWriter_ChainsTransformsAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' sink writer is the fixture");
            Assert.SkipUnless(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264)
                && Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformAudioEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.AudioCodec.AAC), "this system has no H.264 and AAC encoders");
            var windows = Clean(File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "SinkTransform.windows.txt")));
            var ours = Clean(Report());
            for (int i = 0; i < Math.Max(windows.Count, ours.Count); i++)
                Assert.True(i < windows.Count && i < ours.Count && windows[i] == ours[i], $"line {i}: Windows '{(i < windows.Count ? windows[i] : "(none)")}', ours '{(i < ours.Count ? ours[i] : "(none)")}'");
        }

#pragma warning disable SYSLIB1045 // the patterns of a test, made once
        private static List<string> Clean(IEnumerable<string> lines)
        {
            var r = new List<string>();
            bool writing = false;
            foreach (var line in lines)
            {
                var l = System.Text.RegularExpressions.Regex.Replace(line, "MF_MT_MPEG_SEQUENCE_HEADER = BLOB\\[\\d+\\] [0-9a-f]+", "MF_MT_MPEG_SEQUENCE_HEADER = (the encoder's)");
                if (l.StartsWith("after five samples")) writing = true;
                else if (!l.StartsWith("  statistics")) writing = false;
                if (l.StartsWith("  statistics:"))
                {
                    int semi = l.IndexOf(';');
                    l = System.Text.RegularExpressions.Regex.Replace(l[..semi], "(encoded|processed) (\\d+)", m => $"{m.Groups[1].Value} {Math.Round(long.Parse(m.Groups[2].Value) / 100.0) * 100}") + l[semi..];
                    if (writing) l = System.Text.RegularExpressions.Regex.Replace(l, "; received (\\d+), encoded \\d+, processed \\d+", "; received $1");
                    if (writing) l = System.Text.RegularExpressions.Regex.Replace(l, "encoded \\d+, processed \\d+, tick", "tick");
                }
                r.Add(l);
            }
            return r;
        }
#pragma warning restore SYSLIB1045

        /// <summary>How the mean bit rate is set: not, through the service before BeginWriting, after it, as SetInputMediaType's encoding parameters.</summary>
        internal enum Way { None, ServiceBefore, ServiceAfter, Parameters }

        private static void SetMean(IntPtr w, uint i, uint b)
        {
            Assert.Equal(0, Service(w, i, IID_ICodecAPI, out var api));
            var k = AVEncCommonMeanBitRate;
            var v = stackalloc byte[24];
            new Span<byte>(v, 24).Clear();
            *(ushort*)v = 19; *(uint*)(v + 8) = b;                                   // VT_UI4
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Fn(api, 9))(api, &k, v));
            MFReader.Release(api);
        }

        /// <summary>The bytes of a file of 25 frames (the tests' picture, with noise), written at 4 Mb/s, the encoder's mean bit rate set the way given.</summary>
        internal static long Written(Way way, uint b = 200_000)
        {
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-sinkxfb-{Environment.ProcessId}-{way}.mp4");
            try
            {
                Assert.Equal(0, MFWriter.Create(path, IntPtr.Zero, out var w));
                var target = VideoType(H264, 320, 240, 4_000_000);
                Assert.Equal(0, MFWriter.AddStream(w, target, out uint i));
                MFReader.Release(target);
                var input = VideoType(NV12, 320, 240);
                IntPtr parameters = IntPtr.Zero;
                if (way == Way.Parameters) { parameters = MFReader.Attributes(); MFReader.SetU32(parameters, AVEncCommonMeanBitRate, b); }
                Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)Fn(w, 4))(w, i, input, parameters));
                MFReader.Release(parameters);
                MFReader.Release(input);
                if (way == Way.ServiceBefore) SetMean(w, i, b);
                Assert.Equal(0, MFWriter.BeginWriting(w));
                if (way == Way.ServiceAfter) SetMean(w, i, b);
                var rng = new Random(7);
                for (int f = 0; f < 25; f++)
                {
                    var frame = MFDecoderTests.Picture(320, 240, f);
                    for (int n = 0; n < frame.Length; n++) frame[n] = (byte)Math.Clamp(frame[n] + rng.Next(-12, 13), 0, 255);
                    var smp = MFWriter.Sample(frame, f * 400_000L, 400_000);
                    Assert.Equal(0, MFWriter.WriteSample(w, i, smp));
                    MFReader.Release(smp);
                }
                Assert.Equal(0, MFWriter.Finalize(w));
                MFReader.Release(w);
                return new FileInfo(path).Length;
            }
            finally { try { File.Delete(path); } catch { } }
        }

        /// <summary>
        /// The mean bit rate as Windows' encoder takes it (probed): the H.264 MFT alone takes it set before its types, or once frames
        /// have come (a change of the running encoder), and not between; so through the writer, of SetInputMediaType's encoding
        /// parameters, and not of the encoder GetServiceForStream hands out before BeginWriting.
        /// </summary>
        [Fact]
        public void TheEncodersMeanBitRate_IsTakenWhenWindowsTakesIt()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows() || Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264), "this system has no H.264 encoder");
            MFReader.Startup();
            var sizes = Enum.GetValues<Way>().ToDictionary(w => w, w => Written(w));
            var alone = Enum.GetValues<Moment>().ToDictionary(m => m, m => Encoded(m));
            string all = string.Join(", ", sizes.Select(kv => $"{kv.Key} {kv.Value}")) + "; alone " + string.Join(", ", alone.Select(kv => $"{kv.Key} {kv.Value}"));
            static bool Same(long a, long b) => Math.Abs(a - b) <= b / 10;
            static bool Lower(long a, long b) => a * 4 < b * 3;
            // (after BeginWriting, Windows' writer starts its encoder on a queue of its own: a value set then is taken or not as the
            // two meet -- probed both ways -- so that way is not asserted)
            Assert.True(Same(sizes[Way.ServiceBefore], sizes[Way.None]) && Lower(sizes[Way.Parameters], sizes[Way.None]), all);
            Assert.True(Lower(alone[Moment.BeforeTypes], alone[Moment.None]) && Same(alone[Moment.AfterTypes], alone[Moment.None])
                && Same(alone[Moment.AfterBeginStreaming], alone[Moment.None]) && Lower(alone[Moment.AfterFirstFrame], alone[Moment.None]), all);
        }

        internal enum Moment { None, BeforeTypes, AfterTypes, AfterBeginStreaming, AfterFirstFrame }

        private static void SetMeanOn(IntPtr t, uint b)
        {
            var iid = IID_ICodecAPI; IntPtr api;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(t, 0))(t, &iid, &api));
            var k = AVEncCommonMeanBitRate;
            var v = stackalloc byte[24];
            new Span<byte>(v, 24).Clear();
            *(ushort*)v = 19; *(uint*)(v + 8) = b;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Fn(api, 9))(api, &k, v));
            MFReader.Release(api);
        }

        /// <summary>The bytes the H.264 encoder MFT alone makes of the 25 frames at 4 Mb/s, its mean bit rate set to 200 kb/s at the moment given.</summary>
        internal static long Encoded(Moment m)
        {
            IntPtr t = MFEncoderProbe.Encoder(new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"), Video, NV12, H264);
            Assert.NotEqual(IntPtr.Zero, t);
            if (m == Moment.BeforeTypes) SetMeanOn(t, 200_000);
            var o = VideoType(H264, 320, 240, 4_000_000);
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, o, 0));
            MFReader.Release(o);
            var i = VideoType(NV12, 320, 240);
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, i, 0));
            MFReader.Release(i);
            if (m == Moment.AfterTypes) SetMeanOn(t, 200_000);
            ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 0x10000000, 0);
            ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 0x10000003, 0);
            if (m == Moment.AfterBeginStreaming) SetMeanOn(t, 200_000);
            long bytes = 0;
            void Take()
            {
                while (true)
                {
                    var info = stackalloc uint[3];
                    ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(t, 7))(t, 0, info);
                    IntPtr mine = (info[0] & 0x100) == 0 ? MFWriter.Sample(new byte[Math.Max(1u << 20, info[1])], 0, 0) : IntPtr.Zero;
                    var odb = stackalloc byte[32];
                    new Span<byte>(odb, 32).Clear();
                    *(IntPtr*)(odb + IntPtr.Size) = mine;
                    uint status;
                    int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
                    if (hr < 0) { MFReader.Release(mine); return; }
                    IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
                    bytes += MFReader.SampleBytes(got, out _).Length;
                    MFReader.Release(got);
                }
            }
            var rng = new Random(7);
            for (int f = 0; f < 25; f++)
            {
                var frame = MFDecoderTests.Picture(320, 240, f);
                for (int n = 0; n < frame.Length; n++) frame[n] = (byte)Math.Clamp(frame[n] + rng.Next(-12, 13), 0, 255);
                var smp = MFWriter.Sample(frame, f * 400_000L, 400_000);
                Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, smp, 0));
                MFReader.Release(smp);
                Take();
                if (f == 0 && m == Moment.AfterFirstFrame) SetMeanOn(t, 200_000);
            }
            ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 1, 0);
            Take();
            MFReader.Release(t);
            return bytes;
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SINKXF") is not { Length: > 0 }, "MF_PROBE_SINKXF=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SINKXF")!, Report());
        }
    }
}
