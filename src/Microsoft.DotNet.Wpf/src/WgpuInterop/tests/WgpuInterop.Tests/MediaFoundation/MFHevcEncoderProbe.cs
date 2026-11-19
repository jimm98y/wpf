// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' HEVC encoder MFT (MF_PROBE_HEVC=<file> writes the report): what MFTEnumEx lists of HEVC encoders, whether
// CLSID_MSH265EncoderMFT is made, and the sink writer of an HEVC stream: the transform it puts before the sink, the file.
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
    public sealed unsafe class MFHevcEncoderProbe
    {
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* ppv);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")] private static extern int WinMFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), HEVC = new("43564548-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Clsid = new("f2f84074-8bca-40bd-9159-e880f673dd3b");
        private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

        internal static List<string> Report()
        {
            MFReader.Startup();
            if (OperatingSystem.IsWindows()) CoInitializeEx(IntPtr.Zero, 0);
            var log = new List<string> { "==== enumerated" };
            log.AddRange(MFEncoderProbe.Enumerated(MFEncoderProbe.VideoEncoderCategory, Video, NV12, HEVC));
            log.AddRange(MFEncoderProbe.Enumerated(MFEncoderProbe.VideoEncoderCategory, Video, null, HEVC));
            log.Add("==== by CLSID");
            var c = Clsid; var iid = IID_IMFTransform; IntPtr t;
            int hr = OperatingSystem.IsWindows() ? CoCreateInstance(&c, IntPtr.Zero, 1, &iid, &t) : MFClasses.Create(&c, &iid, &t);
            log.Add($"  create {Hr(hr)}");
            if (hr >= 0) MFReader.Release(t);
            log.AddRange(Details());
            log.AddRange(Writer());
            return log;
        }

        private static IntPtr Out(uint w, uint h, uint? profile = null)
        {
            var o = MFReader.MediaType();
            MFReader.SetGuid(o, MAJOR, Video); MFReader.SetGuid(o, SUB, HEVC);
            MFReader.SetU64(o, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(w, h));
            MFReader.SetU64(o, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(30, 1));
            MFReader.SetU32(o, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
            MFReader.SetU32(o, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            if (profile is { } p) MFReader.SetU32(o, new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036"), p);
            return o;
        }

        private static IEnumerable<int> HevcNals(byte[] b)
        {
            for (int i = 0; i + 4 < b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && (b[i + 2] == 1 || b[i + 2] == 0 && b[i + 3] == 1)) { int at = b[i + 2] == 1 ? i + 3 : i + 4; yield return (b[at] >> 1) & 0x3F; i = at; }
        }

        /// <summary>The encoder MFTEnumEx gives first: its activate, attributes, interfaces, types, ICodecAPI, ten frames.</summary>
        private static List<string> Details()
        {
            var log = new List<string> { "==== the encoder enumerated" };
            var i0 = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = NV12 };
            var o0 = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = HEVC };
            IntPtr* acts; uint n; var cat = MFEncoderProbe.VideoEncoderCategory;
            int hr = OperatingSystem.IsWindows() ? WinMFTEnumEx(cat, 0x3F, &i0, &o0, &acts, &n) : MFExports.MFTEnumEx(cat, 0x3F, &i0, &o0, &acts, &n);
            if (hr < 0 || n == 0) { log.Add("  none"); return log; }
            IntPtr a = acts[0];
            for (uint k = 1; k < n; k++) MFReader.Release(acts[k]);
            Marshal.FreeCoTaskMem((IntPtr)acts);
            log.Add("  activate:"); log.AddRange(MFReader.Dump(a).Select(l => "    " + l));
            foreach (var (what, key) in new[] { ("input types", new Guid("4276c9b1-759d-4bf3-9cd0-0d723d138f96")), ("output types", new Guid("8eae8cf3-a44f-4306-ba5c-bf5dda242818")) })
            {
                var k = key; uint size;
                if (((delegate* unmanaged<IntPtr, Guid*, uint*, int>)MFReader.Fn(a, 14))(a, &k, &size) < 0) continue;
                var blob = new byte[size];
                fixed (byte* bp = blob) ((delegate* unmanaged<IntPtr, Guid*, byte*, uint, uint*, int>)MFReader.Fn(a, 15))(a, &k, bp, size, &size);
                var subs = Enumerable.Range(0, blob.Length / 32).Select(x => MFReader.Name(new Guid(blob.AsSpan(x * 32 + 16, 16))));
                log.Add($"  {what}: {string.Join(", ", subs)}");
            }
            var iid = IID_IMFTransform; IntPtr t;
            hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(a, 33))(a, &iid, &t);
            log.Add($"  activate {Hr(hr)}");
            if (hr < 0) { MFReader.Release(a); return log; }
            foreach (var (name, g) in new (string, Guid)[] { ("ICodecAPI", new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")), ("IMFShutdown", new("97ec2ea4-0e42-4937-97ac-9d6d328824e1")),
                ("IMFRealTimeClientEx", new("03910848-ab16-4611-b100-17b88ae2f248")), ("IMFGetService", new("fa993888-4383-415a-a930-dd472a8cf6f7")), ("IPropertyStore", new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")),
                ("IMFMediaEventGenerator", new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")) })
            {
                var q = g; IntPtr p;
                int qhr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(t, 0))(t, &q, &p);
                log.Add($"  {name} {Hr(qhr)}");
                if (qhr >= 0) MFReader.Release(p);
            }
            IntPtr attrs; ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(t, 8))(t, &attrs);
            log.Add("  attributes:"); if (attrs != IntPtr.Zero) { log.AddRange(MFReader.Dump(attrs).Select(l => "    " + l)); MFReader.Release(attrs); }
            uint* ids = stackalloc uint[4];
            log.Add($"  stream limits {Hr(((delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)MFReader.Fn(t, 3))(t, ids, ids + 1, ids + 2, ids + 3))} {ids[0]} {ids[1]} {ids[2]} {ids[3]}");
            var info = stackalloc uint[6];
            log.Add($"  output info, none set {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]}");
            log.AddRange(MFEncoderProbe.AllTypes(t, 14, "output available, before"));
            log.AddRange(MFEncoderProbe.AllTypes(t, 13, "input available, before"));
            MFEncoderProbe.DumpType(t, 14, 0, log, "output");
            log.AddRange(MFEncoderProbe.CodecApi(t));
            foreach (var (what, o) in new[] { ("Main10", Out(320, 240, 2)), ("Main", Out(320, 240, 1)), ("odd size", Out(321, 241)), ("plain", Out(320, 240)) })
            {
                log.Add($"  set output, {what}: test {Hr(MFEncoderProbe.SetOut(t, o, 1))} set {Hr(MFEncoderProbe.SetOut(t, o))}");
                MFReader.Release(o);
            }
            log.AddRange(MFEncoderProbe.AllTypes(t, 13, "input available"));
            MFEncoderProbe.DumpType(t, 13, 0, log, "input");
            IntPtr it = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, 13))(t, 0, 0, &it);
            log.Add($"  set input (available 0) {Hr(MFEncoderProbe.SetIn(t, it))}");
            MFReader.Release(it);
            var inInfo = stackalloc uint[6];
            log.Add($"  input info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 6))(t, 0, inInfo))} flags 0x{inInfo[2]:x} size {inInfo[3]} align {inInfo[5]}");
            log.Add($"  output info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]} align {info[2]}");
            IntPtr cur;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.Add("  output current:"); log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            log.Add($"  begin {Hr(MFEncoderProbe.Message(t, 0x10000000))} start {Hr(MFEncoderProbe.Message(t, 0x10000003))}");
            var sizes = new List<string>();
            for (int k = 0; k < 10; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(320, 240, k), k * 333_333L, 333_333);
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0);
                MFReader.Release(s);
                log.Add($"  input {k} {Hr(hr)}");
                Outputs(t, log);
            }
            log.Add($"  drain {Hr(MFEncoderProbe.Message(t, 1))}");
            Outputs(t, log);
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.Add("  output current after:"); log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            MFReader.Release(t);
            log.Add($"  shutdown {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(a, 34))(a))}");
            MFReader.Release(a);
            return log;
        }

        private static void Outputs(IntPtr t, List<string> log)
        {
            for (int n = 0; n < 30; n++)
            {
                var info = stackalloc uint[3];
                ((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info);
                bool provides = (info[0] & 0x100) != 0;
                IntPtr mine = provides ? IntPtr.Zero : MFWriter.Sample(new byte[Math.Max(info[1], 300_000u)], 0, 0);
                var odb = stackalloc byte[4 * sizeof(IntPtr)];
                new Span<byte>(odb, 4 * sizeof(IntPtr)).Clear();
                *(IntPtr*)(odb + IntPtr.Size) = mine;
                uint status;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)MFReader.Fn(t, 25))(t, 0, 1, odb, &status);
                IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
                if (hr < 0) { log.Add($"    output {Hr(hr)}"); MFReader.Release(mine); break; }
                var b = MFReader.SampleBytes(got, out _);
                long time; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(got, 35))(got, &time);
                var attrs = MFReader.Dump(got).Select(l => l.Split(' ')[0] + "=" + l.Split(' ').Last());
                var nals = HevcNals(b).ToList();
                // the parameter sets, and the picture's kind (IRAP: 16 to 23; the others under 32)
                string kinds = $"{(nals.Contains(32) && nals.Contains(33) && nals.Contains(34) ? "VPS SPS PPS " : "")}{(nals.Any(x => x is >= 16 and <= 23) ? "IRAP" : nals.Any(x => x < 16) ? "non-IRAP" : "none")}";
                log.Add($"    output: time {time} duration {MFReader.Duration(got)} bytes {b.Length > 0} {kinds} [{string.Join(" ", attrs)}]");
                MFReader.Release(got);
            }
        }

        private static List<string> Writer()
        {
            var log = new List<string> { "==== the sink writer, HEVC" };
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-hevc-{Environment.ProcessId}.mp4");
            try
            {
                int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
                log.Add($"  create {Hr(hr)}");
                if (hr < 0) return log;
                var target = MFReader.MediaType();
                MFReader.SetGuid(target, MAJOR, Video); MFReader.SetGuid(target, SUB, HEVC);
                MFReader.SetU64(target, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
                MFReader.SetU64(target, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
                MFReader.SetU32(target, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
                MFReader.SetU32(target, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
                hr = MFWriter.AddStream(w, target, out uint i);
                var input = MFReader.MediaType();
                ((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(target, 32))(target, input);
                MFReader.SetGuid(input, SUB, NV12);
                { var bk = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"); ((delegate* unmanaged<IntPtr, Guid*, int>)MFReader.Fn(input, 19))(input, &bk); }
                MFReader.Release(target);
                int inHr = hr >= 0 ? MFWriter.SetInputMediaType(w, i, input) : hr;
                MFReader.Release(input);
                log.Add($"  add {Hr(hr)}, input {Hr(inHr)}");
                for (uint k = 0; k < 3 && hr >= 0; k++)
                {
                    Guid cat; IntPtr t;
                    int thr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)MFReader.Fn(w, 14))(w, i, k, &cat, &t);
                    log.Add($"  transform {k}: {Hr(thr)}{(thr >= 0 ? $" {cat}" : "")}");
                    if (thr < 0) break;
                    IntPtr attrs;
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(t, 8))(t, &attrs) >= 0 && attrs != IntPtr.Zero) { log.AddRange(MFReader.Dump(attrs).Select(l => "    " + l)); MFReader.Release(attrs); }
                    MFReader.Release(t);
                }
                if (inHr >= 0)
                {
                    log.Add($"  begin {Hr(MFWriter.BeginWriting(w))}");
                    for (int k = 0; k < 10; k++)
                    {
                        var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                        hr = MFWriter.WriteSample(w, i, s);
                        MFReader.Release(s);
                        if (hr < 0) { log.Add($"  write {k} {Hr(hr)}"); break; }
                    }
                    log.Add($"  finalize {Hr(MFWriter.Finalize(w))}");
                }
                MFReader.Release(w);
                log.Add($"  file {(File.Exists(path) ? new FileInfo(path).Length > 500 : false)}");
            }
            finally { try { File.Delete(path); } catch { } }
            return log;
        }

        /// <summary>
        /// The shim's HEVC encoder MFT is Windows' (HevcEncoder.windows.txt): enumerated, its activate, interfaces, attributes,
        /// types, ICodecAPI, what it takes and refuses, a picture out of each in, of the input's time, the parameter sets
        /// first, IRAP clean points; the sink writer putting it before the sink. Not compared: the activate's package (its
        /// path, names, class, architecture, a GUID of each run), the attribute of an object Windows' encoder holds, and
        /// each picture's QP (MFSampleExtension_VideoEncodeQP, which the system's encoders tell not).
        /// </summary>
        [Fact]
        public void TheHevcEncoder_IsWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' HEVC encoder is the fixture");
            Assert.SkipUnless(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H265), "this system has no HEVC encoder");
            var windows = Clean(File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "HevcEncoder.windows.txt")));
            var ours = Clean(Report());
            for (int i = 0; i < Math.Max(windows.Count, ours.Count); i++)
                Assert.True(i < windows.Count && i < ours.Count && windows[i] == ours[i], $"line {i}: Windows '{(i < windows.Count ? windows[i] : "(none)")}', ours '{(i < ours.Count ? ours[i] : "(none)")}'");
        }

        private static readonly string[] s_package = { "7347c815", "957193ad", "9d8b61a8", "bb49bc51", "de106d30", "e626cad1", "592a2a5a" };

        private static List<string> Clean(IEnumerable<string> lines) => lines
            .Where(l => !s_package.Any(k => l.TrimStart().StartsWith(k)))
            .Select(l => l.IndexOf(" b2efe478-", StringComparison.Ordinal) is var at and >= 0 ? l.Remove(at, l.IndexOf(')', at) + 1 - at) : l)
            .ToList();

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_HEVC") is not { Length: > 0 }, "MF_PROBE_HEVC=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_HEVC")!, Report());
        }
    }
}
