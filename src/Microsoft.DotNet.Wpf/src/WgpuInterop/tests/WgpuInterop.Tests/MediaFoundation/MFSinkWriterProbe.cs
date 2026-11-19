// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a sink writer does (MF_PROBE_SINK=<file> writes the report): the encoders MFTEnumEx lists, a sink
// writer of an MP4 file -- H.264 of NV12 frames, AAC of PCM -- each call's result, the file's boxes (a tree,
// the leaf boxes' bytes), and the file read back through the source reader. Windows' writer on Windows, the
// shim's elsewhere.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal static unsafe class MFWriter
    {
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSinkWriterFromURL")] private static extern int WinCreate([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr stream, IntPtr attributes, IntPtr* writer);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateMemoryBuffer")] private static extern int WinMFCreateMemoryBuffer(uint size, IntPtr* b);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateSample")] private static extern int WinMFCreateSample(IntPtr* s);

        public static int Create(string path, IntPtr attributes, out IntPtr writer)
        {
            IntPtr w; int hr;
            if (OperatingSystem.IsWindows()) { MFReader.Startup(); hr = WinCreate(path, IntPtr.Zero, attributes, &w); }
            else fixed (char* p = path) hr = MFExports.MFCreateSinkWriterFromURL(p, IntPtr.Zero, attributes, &w);
            writer = hr >= 0 ? w : IntPtr.Zero;
            return hr;
        }

        public static int AddStream(IntPtr w, IntPtr type, out uint index) { uint i; int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint*, int>)MFReader.Fn(w, 3))(w, type, &i); index = i; return hr; }
        public static int SetInputMediaType(IntPtr w, uint i, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)MFReader.Fn(w, 4))(w, i, type, IntPtr.Zero);
        public static int BeginWriting(IntPtr w) => ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(w, 5))(w);
        public static int WriteSample(IntPtr w, uint i, IntPtr s) => ((delegate* unmanaged<IntPtr, uint, IntPtr, int>)MFReader.Fn(w, 6))(w, i, s);
        public static int Finalize(IntPtr w) => ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(w, 11))(w);

        public static IntPtr Sample(byte[] data, long time, long duration)
        {
            IntPtr buf, s;
            int hr = OperatingSystem.IsWindows() ? WinMFCreateMemoryBuffer((uint)data.Length, &buf) : MFExports.MFCreateMemoryBuffer((uint)data.Length, &buf);
            Assert.True(hr >= 0);
            byte* p; uint max, cur;
            ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)MFReader.Fn(buf, 3))(buf, &p, &max, &cur);
            data.CopyTo(new Span<byte>(p, data.Length));
            ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(buf, 4))(buf);
            ((delegate* unmanaged<IntPtr, uint, int>)MFReader.Fn(buf, 6))(buf, (uint)data.Length);
            hr = OperatingSystem.IsWindows() ? WinMFCreateSample(&s) : MFExports.MFCreateSample(&s);
            Assert.True(hr >= 0);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(s, 42))(s, buf);
            MFReader.Release(buf);
            ((delegate* unmanaged<IntPtr, long, int>)MFReader.Fn(s, 36))(s, time);
            ((delegate* unmanaged<IntPtr, long, int>)MFReader.Fn(s, 38))(s, duration);
            return s;
        }
    }

    public sealed unsafe class MFSinkWriterProbe
    {
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid AAC = new("00001610-0000-0010-8000-00aa00389b71"), PCM = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid CH = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), AVG_BYTES = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), BLOCK = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), MFT_CATEGORY_AUDIO_ENCODER = new("91c64bd0-f91e-4d8c-9276-db248279d975");

        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")] private static extern int WinMFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count);

        private static string Hr(int hr) => $"0x{hr:x8}";

        private static IntPtr Type(Guid major, Guid sub, params (Guid, ulong, bool)[] values)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, major);
            MFReader.SetGuid(t, SUB, sub);
            foreach (var (k, v, wide) in values) if (wide) MFReader.SetU64(t, k, v); else MFReader.SetU32(t, k, (uint)v);
            return t;
        }

        /// <summary>The boxes of an ISO base media file, a line each: type, size; a leaf's bytes (of mdat, the size alone).</summary>
        internal static List<string> Boxes(byte[] f)
        {
            var log = new List<string>();
            var containers = new HashSet<string> { "moov", "trak", "mdia", "minf", "stbl", "dinf", "edts", "udta", "mvex", "moof", "traf" };
            void Walk(int at, int end, int depth)
            {
                while (at + 8 <= end)
                {
                    long size = (uint)(f[at] << 24 | f[at + 1] << 16 | f[at + 2] << 8 | f[at + 3]);
                    string type = Encoding.ASCII.GetString(f, at + 4, 4);
                    int header = 8;
                    if (size == 1) { size = (long)BitConverter.ToUInt64(f.AsSpan(at + 8, 8).ToArray().Reverse().ToArray()); header = 16; }
                    if (size == 0) size = end - at;
                    if (size < header || at + size > end) { log.Add(new string(' ', depth * 2) + $"{type} bad size {size}"); return; }
                    string pad = new string(' ', depth * 2);
                    if (containers.Contains(type)) { log.Add($"{pad}{type} {size}"); Walk(at + header, at + (int)size, depth + 1); }
                    else if (type == "stsd")
                    {
                        log.Add($"{pad}stsd {size} {Convert.ToHexString(f, at + header, 8).ToLowerInvariant()}");
                        // the entry and its boxes
                        int e = at + header + 8;
                        long es = (uint)(f[e] << 24 | f[e + 1] << 16 | f[e + 2] << 8 | f[e + 3]);
                        string et = Encoding.ASCII.GetString(f, e + 4, 4);
                        int fixedLen = et is "avc1" or "hvc1" or "mp4v" ? 78 : 28;
                        log.Add($"{pad}  {et} {es} {Convert.ToHexString(f, e + 8, fixedLen).ToLowerInvariant()}");
                        Walk(e + 8 + fixedLen, e + (int)es, depth + 2);
                    }
                    else if (type == "mdat") log.Add($"{pad}mdat {size}");
                    else
                    {
                        int n = (int)size - header;
                        log.Add($"{pad}{type} {size} {Convert.ToHexString(f, at + header, Math.Min(n, 96)).ToLowerInvariant()}{(n > 96 ? "..." : "")}");
                    }
                    at += (int)size;
                }
            }
            Walk(0, f.Length, 0);
            return log;
        }

        internal static List<string> Report(string path)
        {
            var log = new List<string>();
            foreach (var (cat, name, input, output) in new[] { (MFT_CATEGORY_VIDEO_ENCODER, "video", NV12, H264), (MFT_CATEGORY_AUDIO_ENCODER, "audio", PCM, AAC) })
            {
                var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = name == "video" ? Video : Audio, guidSubtype = input };
                var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = name == "video" ? Video : Audio, guidSubtype = output };
                IntPtr* acts; uint count;
                int h = OperatingSystem.IsWindows() ? WinMFTEnumEx(cat, 1 | 0x20 | 0x40, &i, &o, &acts, &count) : MFExports.MFTEnumEx(cat, 1 | 0x20 | 0x40, &i, &o, &acts, &count);
                log.Add($"{name} encoders {Hr(h)} {(h >= 0 ? count : 0)}");
                for (uint k = 0; h >= 0 && k < count; k++)
                {
                    log.AddRange(MFReader.Dump(acts[k]).Where(l => l.Contains("314ffbae") || l.Contains("6821c42b") || l.Contains("4276c9b1") || l.Contains("8eae8cf3")).Select(l => "  " + l));
                    MFReader.Release(acts[k]);
                }
                if (h >= 0 && count > 0) Marshal.FreeCoTaskMem((IntPtr)acts);
            }

            int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
            log.Add($"create {Hr(hr)}");
            if (hr < 0) return log;
            var vt = Type(Video, H264, (FRAME_SIZE, MF.Pack(128, 96), true), (FRAME_RATE, MF.Pack(25, 1), true), (AVG_BITRATE, 500_000, false), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
            hr = MFWriter.AddStream(w, vt, out uint vi);
            MFReader.Release(vt);
            log.Add($"add video {Hr(hr)} index {vi}");
            var vin = Type(Video, NV12, (FRAME_SIZE, MF.Pack(128, 96), true), (FRAME_RATE, MF.Pack(25, 1), true), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
            log.Add($"video input {Hr(MFWriter.SetInputMediaType(w, vi, vin))}");
            MFReader.Release(vin);
            var at = Type(Audio, AAC, (CH, 2, false), (RATE, 44100, false), (BITS, 16, false), (AVG_BYTES, 16000, false));
            hr = MFWriter.AddStream(w, at, out uint ai);
            MFReader.Release(at);
            log.Add($"add audio {Hr(hr)} index {ai}");
            var ain = Type(Audio, PCM, (CH, 2, false), (RATE, 44100, false), (BITS, 16, false), (BLOCK, 4, false), (AVG_BYTES, 176400, false));
            log.Add($"audio input {Hr(MFWriter.SetInputMediaType(w, ai, ain))}");
            MFReader.Release(ain);
            log.Add($"begin {Hr(MFWriter.BeginWriting(w))}");
            // ten frames; 0.4 s of the tone in 1024-frame pieces
            var tone = File.ReadAllBytes(MFSourceReaderProbe.AudioPath("tone.wav")).AsSpan(44).ToArray();
            int audioFrames = 17640, written = 0;
            for (int k = 0; k < 10; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                int h = MFWriter.WriteSample(w, vi, s);
                MFReader.Release(s);
                if (h < 0) { log.Add($"write video {k} {Hr(h)}"); break; }
                while (written < audioFrames && written * 10_000_000L / 44100 < (k + 1) * 400_000L)
                {
                    int n = Math.Min(1024, audioFrames - written);
                    var a = MFWriter.Sample(tone.AsSpan(written * 4, n * 4).ToArray(), written * 10_000_000L / 44100, n * 10_000_000L / 44100);
                    h = MFWriter.WriteSample(w, ai, a);
                    MFReader.Release(a);
                    if (h < 0) { log.Add($"write audio {written} {Hr(h)}"); break; }
                    written += n;
                }
            }
            log.Add($"finalize {Hr(MFWriter.Finalize(w))}");
            MFReader.Release(w);
            var file = File.ReadAllBytes(path);
            log.Add($"file {file.Length}");
            log.AddRange(Boxes(file));
            log.Add("---- read back");
            log.AddRange(MFSourceReaderProbe.VideoReport(path));
            log.Add("---- read back audio");
            log.AddRange(MFSourceReaderProbe.AudioReport(path));
            return log;
        }

        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSinkWriterFromURL")] private static extern int WinCreateOn([MarshalAs(UnmanagedType.LPWStr)] string? url, IntPtr stream, IntPtr attributes, IntPtr* writer);
        private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca"), MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

        private static int CreateOn(string? url, IntPtr stream, IntPtr attributes, out IntPtr writer)
        {
            IntPtr w; int hr;
            if (OperatingSystem.IsWindows()) { MFReader.Startup(); hr = WinCreateOn(url, stream, attributes, &w); }
            else fixed (char* p = url) hr = MFExports.MFCreateSinkWriterFromURL(url == null ? null : p, stream, attributes, &w);
            writer = hr >= 0 ? w : IntPtr.Zero;
            return hr;
        }

        /// <summary>What the sink writer does with a byte stream of the application's: what it asks of it, what it writes where.</summary>
        internal static List<string> ByteStreamReport()
        {
            var log = new List<string>();
            var cases = new (string Name, string? Url, uint Caps, bool ContainerType)[]
            {
                ("no URL, no container type", null, 7, false),
                ("no URL, MPEG-4 container type", null, 7, true),
                ("URL x.mp4", "x.mp4", 7, false),
                ("URL x.wmv", "x.wmv", 7, false),
                ("read only", "x.mp4", 1, false),
                ("not seekable", "x.mp4", 3, false),
            };
            foreach (var c in cases)
            {
                log.Add($"==== {c.Name}");
                var spy = new SpyByteStream(c.Caps);
                IntPtr attrs = IntPtr.Zero;
                if (c.ContainerType) { attrs = MFReader.Attributes(); MFReader.SetGuid(attrs, MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4); }
                int hr = CreateOn(c.Url, spy.Pointer, attrs, out var w);
                MFReader.Release(attrs);
                log.Add($"create {Hr(hr)}");
                log.Add($"  calls: {string.Join(", ", spy.Calls)}"); spy.Calls.Clear();
                if (hr >= 0)
                {
                    var vt = Type(Video, H264, (FRAME_SIZE, MF.Pack(128, 96), true), (FRAME_RATE, MF.Pack(25, 1), true), (AVG_BITRATE, 500_000, false), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
                    hr = MFWriter.AddStream(w, vt, out uint vi);
                    MFReader.Release(vt);
                    var vin = Type(Video, NV12, (FRAME_SIZE, MF.Pack(128, 96), true), (FRAME_RATE, MF.Pack(25, 1), true), (INTERLACE, 2, false), (PAR, MF.Pack(1, 1), true));
                    log.Add($"add {Hr(hr)}, input {Hr(MFWriter.SetInputMediaType(w, vi, vin))}");
                    MFReader.Release(vin);
                    log.Add($"begin {Hr(MFWriter.BeginWriting(w))}");
                    log.Add($"  calls: {string.Join(", ", spy.Calls)}"); spy.Calls.Clear();
                    for (int k = 0; k < 5; k++)
                    {
                        var smp = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                        int h = MFWriter.WriteSample(w, vi, smp);
                        MFReader.Release(smp);
                        if (h < 0) { log.Add($"write {k} {Hr(h)}"); break; }
                    }
                    log.Add($"  calls while writing: {spy.Calls.Count(x => x.StartsWith("Write") || x.StartsWith("BeginWrite"))} writes");
                    spy.Calls.Clear();
                    log.Add($"finalize {Hr(MFWriter.Finalize(w))}");
                    log.Add($"  calls: {string.Join(", ", spy.Calls.Select(x => x.StartsWith("Write") || x.StartsWith("BeginWrite") ? string.Join(' ', x.Split(' ').Select((wd, i) => i is 1 or 3 ? "n" : wd)) : x))}");
                    MFReader.Release(w);
                    log.Add($"  calls at the release: {string.Join(", ", spy.Calls.Skip(spy.Calls.Count))}");
                    var bytes = spy.Bytes();
                    log.Add($"  bytes {bytes.Length > 0}, boxes: {string.Join(" ", Boxes(bytes).Where(l => !l.StartsWith(" ")).Select(l => l.Split(' ')[0]))}");
                }
                spy.Release();
            }
            return log;
        }

        /// <summary>
        /// The sink writer on a byte stream of the application's, as Windows': the answers without a URL or container type, of a
        /// stream it cannot write and seek; what it asks of the stream and when; the file's boxes; Close at the end. The writes'
        /// count and sizes are the encoder's and the chunks' (Windows' sink writes a sample at a time): a run of them is one.
        /// Windows writes ASF to a .wmv URL, which the shim does not (MF_E_UNSUPPORTED_FORMAT): that case not compared.
        /// </summary>
        [Fact]
        public void TheSinkWriter_WritesToAByteStreamAsWindowsDoes()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows() && !Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264), "no H.264 encoder here");
            static List<string> Clean(IEnumerable<string> lines)
            {
                var r = new List<string>();
                bool skip = false;
                foreach (var l in lines)
                {
                    if (l.StartsWith("==== ")) skip = l.Contains("wmv");
                    if (skip) continue;
                    if (!l.StartsWith("  calls: ")) { r.Add(l); continue; }
                    // runs of writes (each BeginWrite, EndWrite, GetCurrentPosition, and the positions asked between) as one
                    var o = new List<string>();
                    foreach (var c in l["  calls: ".Length..].Split(", "))
                    {
                        string t = c is "EndWrite" or "GetCurrentPosition" ? "" : c.StartsWith("BeginWrite") ? "writes" : c.StartsWith("SetCurrentPosition ") && c != "SetCurrentPosition 63" ? "SetCurrentPosition (the end)" : c;
                        if (t.Length == 0 || o.Count > 0 && o[^1] == t && t == "writes") continue;
                        o.Add(t);
                    }
                    r.Add("  calls: " + string.Join(", ", o));
                }
                return r;
            }
            var w = Clean(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "SinkByteStream.windows.txt")));
            var o = Clean(ByteStreamReport());
            for (int i = 0; i < Math.Max(w.Count, o.Count); i++)
                Assert.True(i < w.Count && i < o.Count && w[i] == o[i], $"line {i}: Windows '{(i < w.Count ? w[i] : "(none)")}', ours '{(i < o.Count ? o[i] : "(none)")}'");
        }

        [Fact]
        public void ProbeByteStream()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SINKBS") is not { Length: > 0 }, "MF_PROBE_SINKBS=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SINKBS")!, ByteStreamReport());
        }

        /// <summary>
        /// A box's line with what of it is the encoder's or the moment's left out: sizes (of the samples' boxes and their
        /// parents), the samples' own tables, the times of creation, the encoders' configurations.
        /// </summary>
        private static string Shape(string line)
        {
            var parts = line.Split(' ');
            int indent = line.Length - line.TrimStart().Length;
            string type = parts[indent], pad = new string(' ', indent);
            string data = parts.Length > indent + 2 ? parts[indent + 2] : "";
            switch (type)
            {
                case "ftyp": case "uuid": case "hdlr": case "vmhd": case "smhd": case "dref": case "Xtra": case "meta": return line;
                case "stsd": return $"{pad}stsd {data}";
                // avc1 and mp4a: the entry's fields (sizes, names) whole
                case "avc1": case "mp4a": return $"{pad}{type} {data}";
                case "mvhd": return $"{pad}mvhd timescale {data.Substring(24, 8)}";                      // after version, flags, created, modified
                case "tkhd": return $"{pad}tkhd flags {data.Substring(0, 8)} id {data.Substring(24, 8)} {data.Substring(data.Length - 16)}";
                case "mdhd": return $"{pad}mdhd timescale {data.Substring(24, 8)} language {data.Substring(40, 4)}";
                // the descriptors' tags and their four-byte length forms, the ES id and flags, the object and stream type (not the
                // lengths: the AudioSpecificConfig is the encoder's)
                case "esds": return $"{pad}esds {data.Substring(0, 16)} {data.Substring(18, 14)} {data.Substring(34, 4)}";
                default: return $"{pad}{type}";
            }
        }

        /// <summary>
        /// The shim's sink writer writes the file Windows' writes, but for its encoders' output: the same boxes in the same order,
        /// the same headers, entries and handlers (SinkWriter.windows.txt); and it reads back, the frames all there.
        /// </summary>
        [Fact]
        public void TheSinkWriter_WritesTheFileWindowsWrites()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' sink writer is the fixture");
            Assert.SkipUnless(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264)
                && Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformAudioEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.AudioCodec.AAC), "this system has no H.264 and AAC encoders");
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-sinktest-{Environment.ProcessId}.mp4");
            try
            {
                var report = Report(path);
                foreach (var l in report.Where(l => l.StartsWith("create") || l.StartsWith("add") || l.EndsWith(" input 0x00000000") || l.StartsWith("begin") || l.StartsWith("finalize")))
                    Assert.Contains("0x00000000", l);
                var boxes = Boxes(File.ReadAllBytes(path)).Select(Shape).ToList();
                var windows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "SinkWriter.windows.txt")).Select(Shape).ToList();
                Assert.Equal(string.Join("\n", windows), string.Join("\n", boxes));
                // read back: ten frames, each of its time
                var back = report.SkipWhile(l => l != "---- read back").ToList();
                var frames = back.TakeWhile(l => l != "---- read back audio").Where(l => l.Contains(" picture ")).ToList();
                Assert.Equal(10, frames.Count);
                for (int k = 0; k < 10; k++) Assert.Contains($"time {k * 400_000} duration 400000", frames[k]);
                Assert.Contains(back, l => l.StartsWith("  channel 0: frames ") && int.Parse(l.Split(' ')[5]) >= 17640);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_SINK") is not { Length: > 0 }, "MF_PROBE_SINK=<file> writes the report");
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-sink-{Environment.ProcessId}.mp4");
            // MF_PROBE_READ=<file>: that file read instead (one another's writer wrote); MF_PROBE_KEEP=<file>: the file written kept there
            if (Environment.GetEnvironmentVariable("MF_PROBE_READ") is { Length: > 0 } read)
            {
                var r = new List<string> { "---- read" };
                r.AddRange(MFSourceReaderProbe.VideoReport(read));
                r.Add("---- read audio");
                r.AddRange(MFSourceReaderProbe.AudioReport(read));
                File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SINK")!, r);
                return;
            }
            try { File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_SINK")!, Report(path)); }
            finally
            {
                if (Environment.GetEnvironmentVariable("MF_PROBE_KEEP") is { Length: > 0 } keep) File.Copy(path, keep, true);
                try { File.Delete(path); } catch { }
            }
        }
    }
}
