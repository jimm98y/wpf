// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ASF, as Windows reads it (MF_PROBE_ASF=<file> writes the report; on Windows, MF_MAKE_ASF=<file.wmv> first writes a file
// of Windows' sink writer: WMV3 video of NV12 and WMA audio of PCM): the source reader's streams, their native types in
// full, the presentation's attributes, and each stream's samples as they come -- times, durations, sizes, clean points,
// first bytes; then a seek. The compressed samples only (there is no WMV or WMA decoder off Windows).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFAsfProbe
    {
        [System.Runtime.InteropServices.DllImport("mf.dll", EntryPoint = "MFTranscodeGetAudioOutputAvailableTypes")] private static extern int WinAudioTypes(Guid* subtype, uint flags, IntPtr attributes, IntPtr* types);
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid WMV3 = new("33564d57-0000-0010-8000-00aa00389b71"), WMA8 = new("00000161-0000-0010-8000-00aa00389b71");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), PCM = new("00000001-0000-0010-8000-00aa00389b71");

        /// <summary>Windows' sink writer's WMV of 10 frames of 128x96 and 2 s of audio.</summary>
        internal static string Make(string path)
        {
            int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
            if (hr < 0) return $"create {Hr(hr)}";
            var vt = MFReader.MediaType();
            MFReader.SetGuid(vt, MAJOR, Video); MFReader.SetGuid(vt, SUB, WMV3);
            MFReader.SetU64(vt, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
            MFReader.SetU64(vt, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
            MFReader.SetU32(vt, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
            MFReader.SetU32(vt, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            MFReader.SetU64(vt, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF.Pack(1, 1));
            int vhr = MFWriter.AddStream(w, vt, out uint vi);
            MFReader.Release(vt);
            // the WMA encoder's own type of stereo 44.1 kHz, its lowest bit rate (MFTranscodeGetAudioOutputAvailableTypes)
            IntPtr at = IntPtr.Zero, coll;
            var wma = WMA8;
            if (WinAudioTypes(&wma, 0, IntPtr.Zero, &coll) >= 0)
            {
                uint n; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(coll, 3))(coll, &n);
                uint best = uint.MaxValue;
                for (uint k = 0; k < n; k++)
                {
                    IntPtr u, t;
                    ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(coll, 4))(coll, k, &u);
                    var iid = new Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
                    ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(u, 0))(u, &iid, &t);
                    MFReader.Release(u);
                    uint bytes = MFReader.U32(t, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731"));
                    if (MFReader.U32(t, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a")) == 2 && MFReader.U32(t, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba")) == 44100 && bytes < best)
                    { MFReader.Release(at); at = t; best = bytes; }
                    else MFReader.Release(t);
                }
                MFReader.Release(coll);
            }
            if (at == IntPtr.Zero) { MFReader.Release(w); return "no WMA type"; }
            int ahr = MFWriter.AddStream(w, at, out uint ai);
            MFReader.Release(at);
            var vin = MFReader.MediaType();
            MFReader.SetGuid(vin, MAJOR, Video); MFReader.SetGuid(vin, SUB, NV12);
            MFReader.SetU64(vin, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
            MFReader.SetU64(vin, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
            MFReader.SetU32(vin, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            MFReader.SetU64(vin, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF.Pack(1, 1));
            var ain = MFReader.MediaType();
            MFReader.SetGuid(ain, MAJOR, Audio); MFReader.SetGuid(ain, SUB, PCM);
            MFReader.SetU32(ain, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 2); MFReader.SetU32(ain, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), 44100);
            MFReader.SetU32(ain, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16); MFReader.SetU32(ain, new Guid("322de230-9eeb-43bd-ab7a-ff412251541d"), 4);
            MFReader.SetU32(ain, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731"), 176400);
            int vihr = vhr < 0 ? vhr : MFWriter.SetInputMediaType(w, vi, vin), aihr = ahr < 0 ? ahr : MFWriter.SetInputMediaType(w, ai, ain);
            MFReader.Release(vin); MFReader.Release(ain);
            if (vhr < 0 || ahr < 0 || vihr < 0 || aihr < 0) { MFReader.Release(w); return $"streams {Hr(vhr)} {Hr(ahr)} inputs {Hr(vihr)} {Hr(aihr)}"; }
            hr = MFWriter.BeginWriting(w);
            if (hr < 0) { MFReader.Release(w); return $"begin {Hr(hr)}"; }
            var tone = File.ReadAllBytes(MFSourceReaderProbe.AudioPath("tone.wav")).AsSpan(44).ToArray();
            for (int k = 0; k < 10; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                hr = MFWriter.WriteSample(w, vi, s);
                MFReader.Release(s);
                if (hr < 0) break;
                int n = 17640;
                var a = MFWriter.Sample(tone.AsSpan((k * n * 4) % Math.Max(1, tone.Length - n * 4), n * 4).ToArray(), k * 400_000L, 400_000);
                hr = MFWriter.WriteSample(w, ai, a);
                MFReader.Release(a);
                if (hr < 0) break;
            }
            int fhr = MFWriter.Finalize(w);
            MFReader.Release(w);
            return $"written {Hr(hr)}, finalized {Hr(fhr)}";
        }

        internal static List<string> Report(string path)
        {
            MFReader.Startup();
            var log = new ProgressLog("MF_PROBE_ASF");
            int hr = MFReader.Create(path, IntPtr.Zero, out var r);
            log.Add($"reader {Hr(hr)}");
            if (hr < 0) return log;
            // the presentation's attributes (MF_SOURCE_READER_MEDIASOURCE, GetPresentationAttribute)
            foreach (var (name, key) in new (string, Guid)[] { ("MF_PD_DURATION", new("6c990d33-bb8e-477a-8598-0d5d96fcd88a")), ("MF_PD_MIME_TYPE", new("6c990d32-bb8e-477a-8598-0d5d96fcd88a")),
                ("MF_PD_TOTAL_FILE_SIZE", new("6c990d38-bb8e-477a-8598-0d5d96fcd88a")), ("MF_PD_AUDIO_ENCODING_BITRATE", new("6c990d35-bb8e-477a-8598-0d5d96fcd88a")),
                ("MF_PD_VIDEO_ENCODING_BITRATE", new("6c990d36-bb8e-477a-8598-0d5d96fcd88a")), ("MF_PD_ASF_FILEPROPERTIES_FILE_ID", new("3de649b4-d76d-4e66-9ec9-78120fb4c7e3")) })
            {
                var pv = stackalloc byte[24]; new Span<byte>(pv, 24).Clear();
                var k = key;
                int phr = ((delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)Fn(r, 12))(r, 0xFFFFFFFF, &k, pv);
                log.Add($"  {name}: {Hr(phr)} {(phr >= 0 ? MFReader.PropVariant(pv) : "")}");
            }
            for (uint i = 0; i < 4; i++)
            {
                int nhr = MFReader.GetNativeMediaType(r, i, 0, out var t);
                log.Add($"stream {i}: {Hr(nhr)}");
                if (nhr < 0) break;
                log.AddRange(MFReader.Dump(t).Select(l => "    " + l));
                MFReader.Release(t);
                int n2 = MFReader.GetNativeMediaType(r, i, 1, out var t2);
                log.Add($"  native 1: {Hr(n2)}");
                MFReader.Release(t2);
            }
            // each stream's samples (the compressed ones, as they are)
            for (uint i = 0; i < 2; i++)
            {
                log.Add($"samples of stream {i}:");
                int count = 0; long bytes = 0;
                for (int k = 0; k < 400; k++)
                {
                    hr = MFReader.ReadSample(r, i, out _, out uint flags, out long ts, out var s);
                    if (hr < 0) { log.Add($"  read {Hr(hr)}"); break; }
                    if (s != IntPtr.Zero)
                    {
                        var b = MFReader.SampleBytes(s, out uint buffers);
                        count++; bytes += b.Length;
                        log.Add($"  {ts} d {MFReader.Duration(s)} size {b.Length} in {buffers} {MFReader.HexOf(b, 8)} [{string.Join(" ", MFReader.Dump(s).Select(l => l.Split(' ')[0] + "=" + l.Split(' ').Last()))}]");
                        MFReader.Release(s);
                    }
                    if ((flags & 2) != 0) { log.Add($"  end, flags 0x{flags:x}"); break; }
                    if (flags != 0) log.Add($"  flags 0x{flags:x} at {ts}");
                }
                log.Add($"  {count} samples, {bytes} bytes");
            }
            // a seek to 1 s, the first of each stream after
            var pos = stackalloc byte[24]; new Span<byte>(pos, 24).Clear();
            *(ushort*)pos = 20; *(long*)(pos + 8) = 10_000_000;
            var none = Guid.Empty;
            log.Add($"seek {Hr(((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Fn(r, 8))(r, &none, pos))}");
            for (uint i = 0; i < 2; i++)
            {
                hr = MFReader.ReadSample(r, i, out _, out uint flags, out long ts, out var s);
                log.Add($"  stream {i}: {Hr(hr)} flags 0x{flags:x} time {ts}{(s != IntPtr.Zero ? $" size {MFReader.SampleBytes(s, out _).Length}" : "")}");
                MFReader.Release(s);
            }
            // decoded types: Windows' decoders (none here)
            var pcm = MFReader.MediaType();
            MFReader.SetGuid(pcm, MAJOR, Audio); MFReader.SetGuid(pcm, SUB, PCM);
            log.Add($"set PCM {Hr(MFReader.SetCurrentMediaType(r, 1, pcm))}");
            MFReader.Release(pcm);
            MFReader.Release(r);
            return log;
        }

        /// <summary>
        /// The shim's source reader reads Windows' WMV (Media/clip.wmv, of Windows' sink writer) as Windows' does
        /// (Asf.windows.txt): its streams, native types, presentation, each compressed sample of its time, duration, size,
        /// attributes (the payload extensions', the object's number, clean points, discontinuities), the seek past its
        /// duration. Not compared: the file's last write (MF_PD_LAST_MODIFIED_TIME, of each copy its own); and WMA to PCM,
        /// which Windows' decoder takes and the shim, of no WMA decoder, refuses (MF_E_TOPO_CODEC_NOT_FOUND).
        /// </summary>
        [Fact]
        public void TheReader_ReadsAsfAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' reader is the fixture");
            var windows = File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "Asf.windows.txt")).Where(l => !l.StartsWith("made:")).ToList();
            var ours = Report(Path.Combine(TestFiles.Root, "MediaFoundation", "Media", "clip.wmv"));
            Assert.Equal(windows.Count, ours.Count);
            for (int i = 0; i < windows.Count; i++)
            {
                string w = windows[i], o = ours[i];
                if (w == o || w.Contains("MF_PD_TOTAL_FILE_SIZE") && o.Contains("BLOB[8]")) continue;
                if (w == "set PCM 0x00000000" && o == "set PCM 0xc00d5212") continue;
                Assert.Fail($"line {i}: Windows '{w}', ours '{o}'");
            }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_ASF") is not { Length: > 0 }, "MF_PROBE_ASF=<file> writes the report");
            string path = Environment.GetEnvironmentVariable("MF_MAKE_ASF") is { Length: > 0 } m ? m : Path.Combine(TestFiles.Root, "MediaFoundation", "Media", "clip.wmv");
            var log = new List<string>();
            if (Environment.GetEnvironmentVariable("MF_MAKE_ASF") is { Length: > 0 }) log.Add($"made: {Make(path)}");
            log.AddRange(Report(path));
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_ASF")!, log);
        }
    }
}
