// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the source reader does with transforms of the application's (MF_PROBE_XFORM=<file> writes the report;
// ReaderTransform.windows.txt is Windows'): IMFSourceReaderEx::AddTransformForStream with the AAC encoder after
// the PCM of a WAV file and the H.264 encoder after the decoded video of an MP4 -- configured or not, before and
// after the stream's type is set -- what the stream's current type becomes, the transforms listed, the samples
// read, and RemoveAllTransformsForStream.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFReaderTransformProbe
    {
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid VIDEO = new("73646976-0000-0010-8000-00aa00389b71"), AUDIO = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid PCM = new("00000001-0000-0010-8000-00aa00389b71"), AAC = new("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid BYTES = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid VCAT = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), ACAT = new("91c64bd0-f91e-4d8c-9276-db248279d975");

        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static int Add(IntPtr r, uint i, IntPtr t) => ((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Fn(r, 14))(r, i, t);
        private static int RemoveAll(IntPtr r, uint i) => ((delegate* unmanaged<IntPtr, uint, int>)Fn(r, 15))(r, i);

        private static string Current(IntPtr r, uint i)
        {
            int hr = MFReader.GetCurrentMediaType(r, i, out var t);
            if (hr < 0) return Hr(hr);
            var d = string.Join("; ", MFReader.Dump(t));
            MFReader.Release(t);
            return d;
        }

        private static List<string> Transforms(IntPtr r, uint i)
        {
            var log = new List<string>();
            for (uint n = 0; n < 4; n++)
            {
                Guid cat; IntPtr t;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)Fn(r, 16))(r, i, n, &cat, &t);
                if (hr < 0) { log.Add($"  transform {n}: {Hr(hr)}"); break; }
                string c = cat == VCAT ? "video encoder" : cat == ACAT ? "audio encoder" : cat == new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91") ? "video decoder" : cat == new Guid("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7") ? "audio decoder" : cat.ToString();
                log.Add($"  transform {n}: {c}");
                MFReader.Release(t);
            }
            return log;
        }

        private static List<string> Reads(IntPtr r, uint i, int max)
        {
            var log = new List<string>();
            int n = 0; long bytes = 0;
            for (int k = 0; k < 400; k++)
            {
                int hr = MFReader.ReadSample(r, i, out _, out uint flags, out long time, out var s);
                if (hr < 0) { log.Add($"  read {Hr(hr)} flags 0x{flags:x}"); break; }
                if (s == IntPtr.Zero) { log.Add($"  read: flags 0x{flags:x} no sample ({n} samples, {bytes} bytes)"); if ((flags & 2) != 0) break; continue; }
                var b = MFReader.SampleBytes(s, out _);
                bytes += b.Length;
                if (n < max) log.Add($"  read: flags 0x{flags:x} time {time} duration {MFReader.Duration(s)} [{string.Join(" ", MFReader.Dump(s).Where(l => !l.StartsWith("8294da66") && !l.StartsWith("b2efe478")).Select(l => l.Split(' ')[0]))}]");
                if ((flags & 0x20) != 0) log.Add($"    current now: {Current(r, i)}");
                MFReader.Release(s);
                n++;
            }
            return log;
        }

        private static IntPtr AacOut(uint ch, uint rate, uint bytes)
        {
            var o = MFReader.MediaType();
            MFReader.SetGuid(o, MAJOR, AUDIO); MFReader.SetGuid(o, SUB, AAC);
            MFReader.SetU32(o, CHANNELS, ch); MFReader.SetU32(o, RATE, rate); MFReader.SetU32(o, BITS, 16); MFReader.SetU32(o, BYTES, bytes);
            return o;
        }

        internal static List<string> Report()
        {
            var log = new List<string>();
            MFReader.Startup();
            string wav = MFSourceReaderProbe.AudioPath("tone.wav");
            // ---- audio: the AAC encoder after the WAV's PCM ----
            foreach (var (what, configure) in new (string, int)[] { ("unconfigured", 0), ("output set", 1), ("both set", 2) })
            {
                log.Add($"==== wav + AAC encoder, {what}");
                MFReader.Create(wav, IntPtr.Zero, out var r);
                log.Add($"  current before: {Current(r, 0)}");
                var enc = MFEncoderProbe.Encoder(ACAT, AUDIO, PCM, AAC);
                if (configure >= 1) { var o = AacOut(2, 44100, 16000); log.Add($"  encoder output {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(enc, 16))(enc, 0, o, 0))}"); MFReader.Release(o); }
                if (configure >= 2)
                {
                    MFReader.GetCurrentMediaType(r, 0, out var cur);
                    log.Add($"  encoder input {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(enc, 15))(enc, 0, cur, 0))}");
                    MFReader.Release(cur);
                }
                log.Add($"  add {Hr(Add(r, 0, enc))}");
                log.Add($"  current after: {Current(r, 0)}");
                log.AddRange(Transforms(r, 0));
                IntPtr ec;
                int h = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(enc, 17))(enc, 0, &ec);
                log.Add($"  encoder input current {Hr(h)}{(h >= 0 ? ": " + string.Join("; ", MFReader.Dump(ec)) : "")}"); if (h >= 0) MFReader.Release(ec);
                h = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(enc, 18))(enc, 0, &ec);
                log.Add($"  encoder output current {Hr(h)}"); if (h >= 0) MFReader.Release(ec);
                log.AddRange(Reads(r, 0, 4));
                log.Add($"  set position 0 {Hr(MFReader.SetCurrentPosition(r, 0))}");
                log.AddRange(Reads(r, 0, 2));
                log.Add($"  remove all {Hr(RemoveAll(r, 0))}");
                log.Add($"  current after removing: {Current(r, 0)}");
                log.AddRange(Transforms(r, 0));
                MFReader.Release(enc);
                MFReader.Release(r);
            }
            // a transform added after the type is set to float (the reader's converter)
            {
                log.Add("==== wav as float + AAC encoder, output set");
                MFReader.Create(wav, IntPtr.Zero, out var r);
                var f = MFReader.MediaType();
                MFReader.SetGuid(f, MAJOR, AUDIO); MFReader.SetGuid(f, SUB, new Guid("00000003-0000-0010-8000-00aa00389b71"));
                log.Add($"  set float {Hr(MFReader.SetCurrentMediaType(r, 0, f))}");
                MFReader.Release(f);
                var enc = MFEncoderProbe.Encoder(ACAT, AUDIO, PCM, AAC);
                var o = AacOut(2, 44100, 16000); ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(enc, 16))(enc, 0, o, 0); MFReader.Release(o);
                log.Add($"  add {Hr(Add(r, 0, enc))}");
                log.Add($"  current after: {Current(r, 0)}");
                log.AddRange(Transforms(r, 0));
                MFReader.Release(enc);
                MFReader.Release(r);
            }
            // ---- video: the H.264 encoder after the decoded frames ----
            var file = new Mp4TestFile();
            string mp4 = file.WriteTemp("xform");
            foreach (var (what, setNv12) in new[] { ("native type", false), ("NV12 set", true) })
            {
                log.Add($"==== mp4 + H.264 encoder, {what}");
                MFReader.Create(mp4, IntPtr.Zero, out var r);
                if (setNv12)
                {
                    var t = MFReader.MediaType();
                    MFReader.SetGuid(t, MAJOR, VIDEO); MFReader.SetGuid(t, SUB, NV12);
                    log.Add($"  set NV12 {Hr(MFReader.SetCurrentMediaType(r, 1, t))}");
                    MFReader.Release(t);
                }
                var enc = MFEncoderProbe.Encoder(VCAT, VIDEO, NV12, H264);
                var o = MFReader.MediaType();
                MFReader.SetGuid(o, MAJOR, VIDEO); MFReader.SetGuid(o, SUB, H264);
                MFReader.SetU64(o, FRAME_SIZE, MF.Pack(64, 48)); MFReader.SetU64(o, FRAME_RATE, MF.Pack(25, 1));
                MFReader.SetU32(o, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000); MFReader.SetU32(o, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
                log.Add($"  encoder output {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(enc, 16))(enc, 0, o, 0))}");
                MFReader.Release(o);
                log.Add($"  add {Hr(Add(r, 1, enc))}");
                log.Add($"  current after: {string.Join("; ", Current(r, 1).Split("; ").Where(l => !l.StartsWith("MF_MT_MPEG_SEQUENCE_HEADER")))}");
                log.AddRange(Transforms(r, 1));
                MFReader.SetStreamSelection(r, 0, false);
                log.AddRange(Reads(r, 1, 3));
                MFReader.Release(enc);
                MFReader.Release(r);
            }
            // ---- an effect of the application's (SpyEffect): what the reader asks of it ----
            var FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71"); var RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71"); var YUY2 = new Guid("32595559-0000-0010-8000-00aa00389b71");
            var spies = new (string Name, bool Video, Guid? Set, Guid[] Accepts, Guid[] Offers, bool Provides)[]
            {
                ("wav, effect of PCM", false, null, new[] { PCM }, new[] { PCM }, false),
                ("wav, effect of float", false, null, new[] { FLOAT }, new[] { FLOAT }, false),
                ("wav as float, effect of PCM", false, FLOAT, new[] { PCM }, new[] { PCM }, false),
                ("wav as float, effect of float, providing", false, FLOAT, new[] { FLOAT }, new[] { FLOAT }, true),
                ("mp4 as NV12, effect of NV12, providing", true, NV12, new[] { NV12 }, new[] { NV12 }, true),
                ("mp4 as NV12, effect of NV12", true, NV12, new[] { NV12 }, new[] { NV12 }, false),
                ("mp4 native, effect of NV12", true, null, new[] { NV12 }, new[] { NV12 }, true),
                ("mp4 as NV12, effect of RGB32", true, NV12, new[] { RGB32 }, new[] { RGB32 }, true),
                ("mp4 as NV12, effect of YUY2 or NV12", true, NV12, new[] { YUY2, NV12 }, new[] { YUY2, NV12 }, true),
            };
            foreach (var sp in spies)
            {
                log.Add($"==== {sp.Name}");
                uint stream = sp.Video ? 1u : 0u;
                MFReader.Create(sp.Video ? mp4 : wav, IntPtr.Zero, out var r);
                if (sp.Video) MFReader.SetStreamSelection(r, 0, false);
                if (sp.Set is Guid set)
                {
                    var t = MFReader.MediaType();
                    MFReader.SetGuid(t, MAJOR, sp.Video ? VIDEO : AUDIO); MFReader.SetGuid(t, SUB, set);
                    log.Add($"  set {MFReader.Name(set)} {Hr(MFReader.SetCurrentMediaType(r, stream, t))}");
                    MFReader.Release(t);
                }
                var spy = new SpyEffect(sp.Video ? VIDEO : AUDIO, sp.Accepts, sp.Offers, sp.Provides);
                log.Add($"  add {Hr(Add(r, stream, spy.Pointer))}");
                log.Add($"  calls: {Collapse(spy.Calls)}"); spy.Calls.Clear();
                log.Add($"  current after: {string.Join("; ", Current(r, stream).Split("; ").Where(l => l.Contains("SUBTYPE") || l.Contains("FRAME_SIZE") || l.Contains("BITS_PER") || l.StartsWith("0x")))}");
                log.AddRange(Transforms(r, stream));
                log.AddRange(Reads(r, stream, 2));
                log.Add($"  calls: {Collapse(spy.Calls)}; inputs {spy.Inputs}"); spy.Calls.Clear();
                log.Add($"  set position 0 {Hr(MFReader.SetCurrentPosition(r, 0))}");
                log.Add($"  calls: {Collapse(spy.Calls)}"); spy.Calls.Clear();
                var t2 = MFReader.MediaType();
                MFReader.SetGuid(t2, MAJOR, sp.Video ? VIDEO : AUDIO); MFReader.SetGuid(t2, SUB, sp.Video ? NV12 : PCM);
                log.Add($"  set {(sp.Video ? "NV12" : "PCM")} with the effect {Hr(MFReader.SetCurrentMediaType(r, stream, t2))}");
                MFReader.Release(t2);
                log.Add($"  calls: {Collapse(spy.Calls)}"); spy.Calls.Clear();
                log.Add($"  current: {string.Join("; ", Current(r, stream).Split("; ").Where(l => l.Contains("SUBTYPE") || l.StartsWith("0x")))}");
                log.Add($"  remove all {Hr(RemoveAll(r, stream))}");
                log.Add($"  calls: {Collapse(spy.Calls)}"); spy.Calls.Clear();
                log.AddRange(Transforms(r, stream));
                MFReader.Release(r);
                log.Add($"  calls at the reader's release: {Collapse(spy.Calls)}");
                spy.Release();
            }
            try { System.IO.File.Delete(mp4); } catch { }
            return log;
        }

        /// <summary>The calls, runs of the same one as one with its count.</summary>
        private static string Collapse(List<string> calls)
        {
            var r = new List<string>();
            for (int i = 0; i < calls.Count;)
            {
                int j = i;
                while (j < calls.Count && calls[j] == calls[i]) j++;
                r.Add(j - i > 1 ? $"{calls[i]} x{j - i}" : calls[i]);
                i = j;
            }
            return string.Join(", ", r);
        }

        /// <summary>
        /// The report as Windows': every call the reader makes of the effect, in order; the types; the transforms listed; the
        /// samples read. Of decoded video, the samples' attributes are the decoder's (Windows' its own, and the discontinuity it
        /// marks every key frame of the file with): not compared.
        /// </summary>
        [Fact]
        public void TheReader_TakesTheApplicationsTransformsAsWindowsReaderDoes()
        {
            var windows = System.IO.File.ReadAllLines(System.IO.Path.Combine(TestFiles.Root, "MediaFoundation", "ReaderTransform.windows.txt"));
            var ours = Report();
            static List<string> Clean(IEnumerable<string> lines)
            {
                var r = new List<string>();
                string section = "";
                foreach (var l in lines)
                {
                    if (l.StartsWith("==== ")) section = l;
                    bool decodedVideo = section.Contains("mp4") && !section.Contains("native");
                    r.Add(decodedVideo && l.StartsWith("  read: flags") && l.Contains('[') ? l[..l.IndexOf('[')] : l);
                }
                return r;
            }
            var w = Clean(windows); var o = Clean(ours);
            for (int i = 0; i < Math.Max(w.Count, o.Count); i++)
                Assert.True(i < w.Count && i < o.Count && w[i] == o[i], $"line {i}: Windows '{(i < w.Count ? w[i] : "(none)")}', ours '{(i < o.Count ? o[i] : "(none)")}'");
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_XFORM") is not { Length: > 0 }, "MF_PROBE_XFORM=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_XFORM")!, Report());
        }
    }
}
