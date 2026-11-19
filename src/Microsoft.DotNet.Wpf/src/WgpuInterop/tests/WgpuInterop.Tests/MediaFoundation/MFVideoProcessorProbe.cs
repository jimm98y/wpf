// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' video processor MFT and color converter DSP, as an application makes them by CLSID (MF_PROBE_VPROC=<file>
// writes the report): the interfaces each answers, its streams and attributes, the types it offers before and after an
// input type, the output types it takes, and what it makes of a picture: converted, scaled. Windows' own on Windows
// (CoCreateInstance), the shim's elsewhere (MFClasses, what the shim's CoCreateInstance makes).
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
    public sealed unsafe class MFVideoProcessorProbe
    {
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* ppv);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        internal static readonly Guid VideoProcessorClsid = new("88753b26-5b24-49bd-b2e7-0c445c78c982"), ColorConverterClsid = new("98230571-0087-4204-b020-3282538e57d3");

        private static Guid Fourcc(string s) => new((uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static readonly Guid NV12 = Fourcc("NV12"), I420 = Fourcc("I420"), IYUV = Fourcc("IYUV"), YV12 = Fourcc("YV12"), YUY2 = Fourcc("YUY2"), UYVY = Fourcc("UYVY");
        private static readonly Guid RGB32 = new("00000016-0000-0010-8000-00aa00389b71"), ARGB32 = new("00000015-0000-0010-8000-00aa00389b71"), RGB24 = new("00000014-0000-0010-8000-00aa00389b71");

        private static readonly (string Name, Guid Iid)[] s_interfaces =
        {
            ("IMFTransform", new("bf94c121-5b05-4e6f-8000-ba598961414d")), ("IMediaObject", new("d8ad0f58-5494-4102-97c5-ec798e59bcf4")),
            ("IPropertyStore", new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")), ("IMFVideoProcessorControl", new("a3f675d5-6119-4f7f-a100-1d8b280f0efb")),
            ("IMFVideoProcessorControl2", new("bde633d3-e1dc-4a7f-a693-bbae399c4a20")), ("IMFRealTimeClientEx", new("03910848-ab16-4611-b100-17b88ae2f248")),
            ("IMFGetService", new("fa993888-4383-415a-a930-dd472a8cf6f7")), ("IMFAttributes", new("2cd2d921-c447-44a7-a13c-4adabfc247e3")),
            ("IWMColorConvProps", new("e7e9984f-f09f-4da4-903f-6e2e0efe56b5")), ("IMFShutdown", new("97ec2ea4-0e42-4937-97ac-9d6d328824e1")),
        };

        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);

        private static int Create(Guid clsid, out IntPtr t)
        {
            var iid = s_interfaces[0].Iid; IntPtr p; int hr;
            if (OperatingSystem.IsWindows()) hr = CoCreateInstance(&clsid, IntPtr.Zero, 1, &iid, &p);
            else hr = MFClasses.Create(&clsid, &iid, &p);
            t = hr >= 0 ? p : IntPtr.Zero;
            return hr;
        }

        private static IntPtr Type(Guid sub, uint w, uint h)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Video); MFReader.SetGuid(t, SUB, sub);
            MFReader.SetU64(t, FRAME_SIZE, MF.Pack(w, h)); MFReader.SetU64(t, FRAME_RATE, MF.Pack(25, 1));
            MFReader.SetU32(t, INTERLACE, 2); MFReader.SetU64(t, PAR, MF.Pack(1, 1));
            return t;
        }

        private static string Short(IntPtr t)
        {
            var (w, h) = MF.Unpack(MFReader.U64(t, FRAME_SIZE));
            uint n; ((delegate* unmanaged<IntPtr, uint*, int>)Fn(t, 30))(t, &n);
            return $"{MFReader.Name(MFReader.G(t, SUB))}{(w > 0 ? $" {w}x{h}" : "")} ({n} attributes)";
        }

        private static List<string> Available(IntPtr t, int slot, string what, bool full = false)
        {
            var log = new List<string>();
            for (uint n = 0; n < 200; n++)
            {
                IntPtr type;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(t, slot))(t, 0, n, &type);
                if (hr < 0) { log.Add($"  {what} {n}: {Hr(hr)}"); break; }
                log.Add($"  {what} {n}: {Short(type)}");
                if (full && n == 0) log.AddRange(MFReader.Dump(type).Select(l => "    " + l));
                MFReader.Release(type);
            }
            return log;
        }

        private static string Info(IntPtr t, int slot)
        {
            var info = stackalloc uint[6];
            new Span<uint>(info, 6).Clear();
            int hr = ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(t, slot))(t, 0, info);
            return slot == 6 ? $"{Hr(hr)} latency {(*(long*)info)} flags 0x{info[2]:x} size {info[3]} lookahead {info[4]} align {info[5]}" : $"{Hr(hr)} flags 0x{info[0]:x} size {info[1]} align {info[2]}";
        }

        /// <summary>A picture of the subtype: NV12 of the tests' (MFDecoderTests.Picture), or RGB32 of a gradient.</summary>
        private static byte[] Picture(Guid sub, int w, int h)
        {
            if (sub == NV12) return MFDecoderTests.Picture(w, h, 1);
            var b = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; b[o] = (byte)(x * 4); b[o + 1] = (byte)(y * 5); b[o + 2] = (byte)((x + y) * 2); b[o + 3] = 255; }
            return b;
        }

        /// <summary>One picture through: ProcessInput, ProcessOutput (a sample of the transform's or of its size), what came out.</summary>
        private static List<string> Convert(IntPtr t, Guid inSub, int w, int h)
        {
            var log = new List<string>();
            var input = MFWriter.Sample(Picture(inSub, w, h), 0, 400_000);
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, input, 0);
            MFReader.Release(input);
            log.Add($"    input {Hr(hr)}");
            for (int k = 0; k < 3 && hr >= 0; k++)
            {
                var info = stackalloc uint[3];
                ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(t, 7))(t, 0, info);
                bool provides = (info[0] & 0x100) != 0;
                IntPtr mine = provides ? IntPtr.Zero : MFWriter.Sample(new byte[Math.Max(info[1], 128u * 96 * 4)], 0, 0);     // room for the largest (a transform that scales not may write the input's size)
                var odb = stackalloc byte[4 * sizeof(IntPtr)];
                new Span<byte>(odb, 4 * sizeof(IntPtr)).Clear();
                *(IntPtr*)(odb + IntPtr.Size) = mine;
                uint status;
                int ohr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
                IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
                if (ohr < 0) { log.Add($"    output {Hr(ohr)}"); MFReader.Release(mine); break; }
                var bytes = MFReader.SampleBytes(got, out uint buffers);
                long time; ((delegate* unmanaged<IntPtr, long*, int>)Fn(got, 35))(got, &time);
                long sum = 0; foreach (var x in bytes) sum += x;
                log.Add($"    output: {bytes.Length} bytes in {buffers}, time {time}, sum {sum}, {Convert16(bytes)}");
                if (bytes.Length == 4608) { long ls = 0; for (int q = 0; q < 3072; q++) ls += bytes[q]; log.Add($"      of the luma {ls}, the chroma's last {System.Convert.ToHexString(bytes, 4608 - 16, 16).ToLowerInvariant()}"); }
                log.Add($"      {string.Join(" ", MFReader.Dump(got).Where(l => !l.Contains("Time")))}");
                MFReader.Release(got);
            }
            return log;
        }

        private static string Convert16(byte[] b) => System.Convert.ToHexString(b, 0, Math.Min(16, b.Length)).ToLowerInvariant();

        /// <summary>One picture through a transform of the types given: the output's bytes, or the failure.</summary>
        private static byte[]? Through(IntPtr t, Guid inSub, Guid outSub, int w, int h, byte[] picture)
        {
            ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 0, 0);
            var i = Type(inSub, (uint)w, (uint)h); var o = Type(outSub, (uint)w, (uint)h);
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, i, 0);
            if (hr >= 0) hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, o, 0);
            MFReader.Release(i); MFReader.Release(o);
            if (hr < 0) return null;
            var input = MFWriter.Sample(picture, 0, 400_000);
            hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, input, 0);
            MFReader.Release(input);
            if (hr < 0) return null;
            IntPtr mine = MFWriter.Sample(new byte[w * h * 4 + 4096], 0, 0);
            var odb = stackalloc byte[4 * sizeof(IntPtr)];
            new Span<byte>(odb, 4 * sizeof(IntPtr)).Clear();
            *(IntPtr*)(odb + IntPtr.Size) = mine;
            uint status;
            hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
            IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
            if (hr < 0) { MFReader.Release(mine); return null; }
            var bytes = MFReader.SampleBytes(got, out _);
            MFReader.Release(got);
            return bytes;
        }

        /// <summary>
        /// Ramps through each class, to read its arithmetic off: NV12 of every luma (chroma neutral), of every U and every V
        /// (luma 128), to RGB32; RGB32 of every red, green and blue, to NV12. Each output's first row (of NV12, and its first
        /// chroma row), in hex.
        /// </summary>
        internal static List<string> Ramps(Guid clsid)
        {
            var log = new List<string>();
            int w = 256, h = 2;
            byte[] Nv12(Func<int, byte> y, Func<int, byte> u, Func<int, byte> v)
            {
                var b = new byte[w * h * 3 / 2];
                for (int r = 0; r < h; r++) for (int x = 0; x < w; x++) b[r * w + x] = y(x);
                for (int x = 0; x < w / 2; x++) { b[w * h + x * 2] = u(x * 2); b[w * h + x * 2 + 1] = v(x * 2); }
                return b;
            }
            byte[] Rgb(int channel)
            {
                var b = new byte[w * h * 4];
                for (int r = 0; r < h; r++) for (int x = 0; x < w; x++) { b[(r * w + x) * 4 + channel] = (byte)x; b[(r * w + x) * 4 + 3] = 255; }
                return b;
            }
            foreach (var (name, pic) in new[] { ("luma", Nv12(x => (byte)x, _ => 128, _ => 128)), ("U", Nv12(_ => 128, x => (byte)x, _ => 128)), ("V", Nv12(_ => 128, _ => 128, x => (byte)x)) })
            {
                Create(clsid, out var t);
                var o = Through(t, NV12, RGB32, w, h, pic);
                MFReader.Release(t);
                log.Add($"  NV12 {name} ramp to RGB32: {(o == null ? "failed" : System.Convert.ToHexString(o, 0, w * 4).ToLowerInvariant())}");
                if (o != null) log.Add($"    last row: {System.Convert.ToHexString(o, w * 4, w * 4).ToLowerInvariant()}");
            }
            foreach (var (name, ch) in new[] { ("blue", 0), ("green", 1), ("red", 2) })
            {
                Create(clsid, out var t);
                var o = Through(t, RGB32, NV12, w, h, Rgb(ch));
                MFReader.Release(t);
                log.Add($"  RGB32 {name} ramp to NV12: {(o == null ? "failed" : System.Convert.ToHexString(o, 0, w).ToLowerInvariant())}");
                if (o != null) log.Add($"    chroma: {System.Convert.ToHexString(o, w * h, w).ToLowerInvariant()}");
            }
            return log;
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            if (OperatingSystem.IsWindows()) CoInitializeEx(IntPtr.Zero, 0);
            var log = new ProgressLog("MF_PROBE_VPROC");
            foreach (var (name, clsid) in new[] { ("video processor", VideoProcessorClsid), ("color converter", ColorConverterClsid) })
            {
                log.Add($"==== {name}");
                int hr = Create(clsid, out var t);
                log.Add($"create {Hr(hr)}");
                if (hr < 0) continue;
                foreach (var (iname, iid) in s_interfaces)
                {
                    var i = iid; IntPtr p;
                    int qhr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(t, 0))(t, &i, &p);
                    log.Add($"  {iname} {Hr(qhr)}");
                    if (qhr >= 0) MFReader.Release(p);
                }
                uint a, b, c, d;
                log.Add($"stream limits {Hr(((delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)Fn(t, 3))(t, &a, &b, &c, &d))} {a} {b} {c} {d}");
                log.Add($"input info {Info(t, 6)}");
                log.Add($"output info {Info(t, 7)}");
                IntPtr attrs;
                int ahr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(t, 8))(t, &attrs);
                log.Add($"attributes {Hr(ahr)}");
                if (ahr >= 0 && attrs != IntPtr.Zero) { log.AddRange(MFReader.Dump(attrs).Select(l => "  " + l)); MFReader.Release(attrs); }
                log.AddRange(Available(t, 13, "input available", true));
                log.AddRange(Available(t, 14, "output available, no input"));

                var nv12 = Type(NV12, 64, 48);
                log.Add($"set input NV12 64x48 {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, nv12, 0))}");
                MFReader.Release(nv12);
                log.AddRange(Available(t, 14, "output available", clsid == VideoProcessorClsid));
                if (clsid == ColorConverterClsid)
                    for (uint n = 0; n < 40; n++)
                    {
                        IntPtr type;
                        if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(t, 14))(t, 0, n, &type) < 0) break;
                        log.Add($"  output {n} in full: {string.Join("; ", MFReader.Dump(type))}");
                        // MF_MT_PALETTE whole
                        var pk = new Guid("6d283f42-9846-4410-afd9-654d503b1a54"); uint size;
                        if (((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Fn(type, 14))(type, &pk, &size) >= 0)
                        {
                            var blob = new byte[size];
                            fixed (byte* bp = blob) ((delegate* unmanaged<IntPtr, Guid*, byte*, uint, uint*, int>)Fn(type, 15))(type, &pk, bp, size, &size);
                            log.Add($"    palette: {System.Convert.ToHexString(blob).ToLowerInvariant()}");
                        }
                        MFReader.Release(type);
                    }
                log.Add($"input info {Info(t, 6)}");
                foreach (var (sub, w, h) in new[] { (RGB32, 64u, 48u), (ARGB32, 64u, 48u), (RGB24, 64u, 48u), (I420, 64u, 48u), (IYUV, 64u, 48u), (YV12, 64u, 48u), (YUY2, 64u, 48u), (UYVY, 64u, 48u), (NV12, 64u, 48u), (NV12, 32u, 24u), (RGB32, 128u, 96u) })
                {
                    // (Windows' color converter, which does not scale, crashes the process at an output of another size: passed over)
                    if (clsid == ColorConverterClsid && w != 64) { log.Add($"  set output {MFReader.Name(sub)} {w}x{h}: passed over"); continue; }
                    // (nor does it live through NV12 to NV12, its identity: the process crashes later, even at its release)
                    if (clsid == ColorConverterClsid && sub == NV12) { log.Add($"  set output {MFReader.Name(sub)} {w}x{h}: passed over"); continue; }
                    var o = Type(sub, w, h);
                    int shr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, o, 0);
                    log.Add($"  set output {MFReader.Name(sub)} {w}x{h} {Hr(shr)}");
                    MFReader.Release(o);
                    if (shr < 0) continue;
                    log.Add($"    output info {Info(t, 7)}");
                    log.AddRange(Convert(t, NV12, 64, 48));
                    log.Add($"    flush {Hr(((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 0, 0))}");
                }
                // RGB32 in, NV12 out
                var rgb = Type(RGB32, 64, 48);
                log.Add($"set input RGB32 64x48 {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, rgb, 0))}");
                MFReader.Release(rgb);
                var on = Type(NV12, 64, 48);
                int nhr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, on, 0);
                MFReader.Release(on);
                log.Add($"  set output NV12 64x48 {Hr(nhr)}");
                if (nhr >= 0) log.AddRange(Convert(t, RGB32, 64, 48));
                MFReader.Release(t);
                log.Add("ramps:");
                log.AddRange(Ramps(clsid));
            }
            return log;
        }

        /// <summary>
        /// The shim's video processor, as an application makes it by CLSID, is Windows' (VideoProcessor.windows.txt): its
        /// interfaces, streams, attributes, types, the outputs it takes, and its pictures to the byte -- but of the upscaled
        /// RGB, whose first pixels are Windows' and whose bytes are within 1 of Windows' on average (Windows' sampling of an
        /// upscale is its own). The color converter alike, to the byte, of its own arithmetic -- but for its IMediaObject (the
        /// DMO), which the shim's has not.
        /// </summary>
        [Fact]
        public void TheVideoProcessorAndColorConverter_AreWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' video processor is the fixture");
            // (the color converter's IMediaObject, which the shim's has not: E_NOINTERFACE)
            var windows = File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "VideoProcessor.windows.txt")).Select(l => l == "  IMediaObject 0x00000000" ? "  IMediaObject 0x80004002" : l).ToList();
            var ours = Report();
            Assert.Equal(windows.Count, ours.Count);
            for (int i = 0; i < windows.Count; i++)
            {
                string w = windows[i], o = ours[i];
                if (w != o && w.StartsWith("    output: 49152 bytes") && o.StartsWith("    output: 49152 bytes"))
                {
                    // the 128x96 RGB32: the same first pixels, the sum within 1 a byte
                    long ws = long.Parse(w.Split("sum ")[1].Split(',')[0]), os = long.Parse(o.Split("sum ")[1].Split(',')[0]);
                    Assert.True(w.Split(", ")[^1] == o.Split(", ")[^1] && Math.Abs(ws - os) < 49152, $"line {i}: Windows '{w}', ours '{o}'");
                    continue;
                }
                Assert.True(w == o, $"line {i}: Windows '{w}', ours '{o}'");
            }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_VPROC") is not { Length: > 0 }, "MF_PROBE_VPROC=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_VPROC")!, Report());
        }
    }
}
